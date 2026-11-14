using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;

namespace JblControl;

public enum ConnectionState { Disconnected, Searching, Connected }

/// <summary>
/// Estado de los audifonos para la UI y la bandeja. Mantiene la conexion,
/// reconecta solo y refresca la bateria periodicamente. Todo corre en el hilo de UI.
/// </summary>
public sealed class DeviceController : INotifyPropertyChanged
{
    static readonly int[] AutoOffSteps = [5, 10, 15, 20, 30, 45, 60, 90, 120];

    readonly JblDevice _dev = new();
    readonly Dispatcher _ui;
    readonly AppSettings _settings;
    readonly SemaphoreSlim _wake = new(0);
    bool _running;
    bool _statusRefreshQueued;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceController(Dispatcher ui, AppSettings settings)
    {
        _ui = ui;
        _settings = settings;
        Audio = new AudioGuard(settings);
        Audio.VolumeLimited += level => ShowVolumeNotice($"Volume lowered to {level}% to protect your hearing.");
        Audio.AudioRestored += () => { _notice = null; Raise(nameof(StatusText)); };
        Audio.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AudioGuard.Restoring)) Raise(nameof(StatusText)); };
        Audio.RestoreFailed += () => { _notice = null; Raise(nameof(StatusText)); };
        _dev.Disconnected += () => _ui.InvokeAsync(OnDisconnected);
        _dev.Unsolicited += d => _ui.InvokeAsync(() => OnUnsolicited(d));

        LeftGestures =
        [
            new GestureSlot(Protocol.GestureLeftTap, "Single tap"),
            new GestureSlot(Protocol.GestureLeftDouble, "Double tap"),
            new GestureSlot(Protocol.GestureLeftTriple, "Triple tap"),
        ];
        RightGestures =
        [
            new GestureSlot(Protocol.GestureRightTap, "Single tap"),
            new GestureSlot(Protocol.GestureRightDouble, "Double tap"),
            new GestureSlot(Protocol.GestureRightTriple, "Triple tap"),
        ];
        foreach (var g in LeftGestures.Concat(RightGestures))
            g.UserChanged += s => Send(() => _dev.SetGestureAsync(s.Id, s.Selected!.Value), refreshStatus: false);
    }

    // ------------------------------------------------------------------ estado

    ConnectionState _state = ConnectionState.Disconnected;
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                BatteryLog.SetConnected(value == ConnectionState.Connected);
                Raise(nameof(IsConnected));
                Raise(nameof(StatusText));
            }
        }
    }

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Historial de bateria de la sesion (solo en memoria).</summary>
    public BatteryLog BatteryLog { get; } = new();

    string? _lastError;
    string? _notice;
    public string StatusText => State switch
    {
        ConnectionState.Connected => Audio.Restoring ? "Connected · restoring Windows audio…" : "Connected",
        ConnectionState.Searching => _notice ?? "Looking for earbuds…",
        _ => _notice ?? (_lastError is null ? "Disconnected" : "Not connected · retrying"),
    };

    public string DeviceName => "JBL LIVE PRO+ TWS";

    string _firmware = "--", _mac = "--";
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    public string Mac { get => _mac; private set => Set(ref _mac, value); }

    int _colorId;
    bool _hasProductInfo;
    /// <summary>Solo se muestra la imagen/color tras leer la informacion del dispositivo.</summary>
    public bool HasProductInfo { get => _hasProductInfo; private set => Set(ref _hasProductInfo, value); }
    public string ColorName => HasProductInfo ? Protocol.ColorOf(_colorId).Name : "--";
    public string ProductImage => "pack://application:,,,/Assets/product/" + Protocol.ColorOf(_colorId).Image;

    /// <summary>Imagen del producto con un auricular resaltado ("left"/"right") o ambos (null).</summary>
    public string ProductImageVariant(string? side)
    {
        var img = Protocol.ColorOf(_colorId).Image;
        if (side != null) img = img.Replace(".png", "_" + side + ".png");
        return "pack://application:,,,/Assets/product/" + img;
    }

    // bateria
    int? _left, _right, _case;
    bool _leftChg, _rightChg, _caseChg;
    public int? LeftBattery { get => _left; private set => Set(ref _left, value); }
    public int? RightBattery { get => _right; private set => Set(ref _right, value); }
    public int? CaseBattery { get => _case; private set => Set(ref _case, value); }
    public bool LeftCharging { get => _leftChg; private set => Set(ref _leftChg, value); }
    public bool RightCharging { get => _rightChg; private set => Set(ref _rightChg, value); }
    public bool CaseCharging { get => _caseChg; private set => Set(ref _caseChg, value); }

    string? _volumeNotice;
    int _noticeSeq;
    public string? VolumeNotice { get => _volumeNotice; private set => Set(ref _volumeNotice, value); }

    /// <summary>Para mostrar el aviso tambien en la bandeja.</summary>
    public event Action<string>? VolumeNoticeShown;

    async void ShowVolumeNotice(string text)
    {
        int seq = ++_noticeSeq;
        VolumeNotice = text;
        VolumeNoticeShown?.Invoke(text);
        await Task.Delay(9000);
        if (seq == _noticeSeq) VolumeNotice = null;
    }

    // ------------------------------------------------------------------ en el oido

    bool? _inEarLeft, _inEarRight;
    public bool? InEarLeft { get => _inEarLeft; private set { if (Set(ref _inEarLeft, value)) Raise(nameof(BothInEar)); } }
    public bool? InEarRight { get => _inEarRight; private set { if (Set(ref _inEarRight, value)) Raise(nameof(BothInEar)); } }
    public bool BothInEar => InEarLeft == true && InEarRight == true;

    public async Task RefreshInEarAsync()
    {
        if (!_dev.IsConnected) { InEarLeft = InEarRight = null; return; }
        try { ApplyInEar(await _dev.GetInEarAsync()); }
        catch (Exception ex) { Log.Write("InEar: " + ex.Message); }
    }

    void ApplyInEar((bool Left, bool Right)? r)
    {
        if (r is null) return;
        InEarLeft = r.Value.Left;
        InEarRight = r.Value.Right;
    }

    // ------------------------------------------------------------------ control de ruido

    NoiseMode _mode;
    public NoiseMode Mode
    {
        get => _mode;
        set
        {
            if (!SetMode(value)) return;
            Send(() => _dev.SetModeAsync(value));
        }
    }

    bool SetMode(NoiseMode m)
    {
        BatteryLog.SetMode(m);
        if (!Set(ref _mode, m, nameof(Mode))) return false;
        Raise(nameof(ModeDescription));
        return true;
    }

    public string ModeDescription => Mode switch
    {
        NoiseMode.Anc => "Blocks outside noise so you only hear your music.",
        NoiseMode.Ambient => "Lets in surrounding sounds so you stay aware.",
        NoiseMode.TalkThru => "TalkThru on: lowers music and boosts voices.",
        _ => "No noise processing.",
    };

    // ------------------------------------------------------------------ Smart Audio & Video

    SmartMode _smart;
    public SmartMode Smart => _smart;

    /// <summary>Guardian del audio de Windows (volumen seguro y reconexion del audio).</summary>
    public AudioGuard Audio { get; }

    /// <summary>
    /// Cambia el modo (tras confirmarlo el usuario). Los auriculares reinician su enlace de audio y
    /// Windows pierde la salida: el guardian la reconecta y limita el volumen cuando vuelve.
    /// </summary>
    public void ChangeSmart(SmartMode mode)
    {
        if (mode == SmartMode.Unknown || mode == _smart || !_dev.IsConnected) return;
        _smart = mode;
        Raise(nameof(Smart));
        Raise(nameof(SmartDescription));
        _notice = "Restoring Windows audio…";
        Audio.BeginRestore();
        Raise(nameof(StatusText));
        Send(() => _dev.SetSmartAsync(mode), refreshStatus: false);
    }

    public string SmartDescription => Smart switch
    {
        SmartMode.Normal => "Balance between sound quality and connection stability.",
        SmartMode.Audio => "Prioritizes sound quality for music.",
        SmartMode.Video => "Reduces latency so audio stays in sync with video.",
        _ => "Unknown mode.",
    };

    // ------------------------------------------------------------------ ecualizador

    int _eqIndex = -1;
    double[] _customGains = new double[10];

    public int EqIndex
    {
        get => _eqIndex;
        set
        {
            if (value < 0 || value > Protocol.EqCustomIndex || !SetEq(value)) return;
            if (value == Protocol.EqCustomIndex)
            {
                var g = (double[])_customGains.Clone();
                Send(() => _dev.WriteCustomEqAsync(g));
            }
            else
            {
                var preset = Protocol.EqPresets[value].Value;
                Send(() => _dev.SetEqAsync(preset));
            }
        }
    }

    bool SetEq(int i)
    {
        if (!Set(ref _eqIndex, i, nameof(EqIndex))) return false;
        Raise(nameof(EqGains));
        Raise(nameof(EqName));
        return true;
    }

    public double[] EqGains => _eqIndex == Protocol.EqCustomIndex ? _customGains
        : _eqIndex >= 0 ? Protocol.EqPresets[_eqIndex].Gains : new double[10];

    public string EqName => _eqIndex == Protocol.EqCustomIndex ? "Custom"
        : _eqIndex >= 0 ? Protocol.EqPresets[_eqIndex].Name : "--";

    /// <summary>Curva del ecualizador personalizado (la guardada en los auriculares).</summary>
    public double[] CustomGains
    {
        get => _customGains;
        private set
        {
            _customGains = value;
            Raise();
            if (_eqIndex == Protocol.EqCustomIndex) Raise(nameof(EqGains));
        }
    }

    /// <summary>Envia la curva editada y activa el ecualizador personalizado.</summary>
    public void ApplyCustomEq(double[] gains)
    {
        var g = gains.Select(x => Util.Clamp(Math.Round(x * 2) / 2, Protocol.EqMinDb, Protocol.EqMaxDb)).ToArray();
        CustomGains = g;
        SetEq(Protocol.EqCustomIndex);
        Raise(nameof(EqGains));
        Send(() => _dev.WriteCustomEqAsync(g));
    }

    // ------------------------------------------------------------------ gestos

    public List<GestureSlot> LeftGestures { get; }
    public List<GestureSlot> RightGestures { get; }

    void ApplyGestures(Dictionary<byte, byte>? map)
    {
        if (map is null) return;
        foreach (var g in LeftGestures.Concat(RightGestures))
            if (map.TryGetValue(g.Id, out var a)) g.SetFromDevice(a);
    }

    // ------------------------------------------------------------------ Find My Buds

    bool _beepLeft, _beepRight;
    public bool BeepLeft { get => _beepLeft; private set => Set(ref _beepLeft, value); }
    public bool BeepRight { get => _beepRight; private set => Set(ref _beepRight, value); }

    public async Task SetBeepAsync(bool left, bool on)
    {
        if (!_dev.IsConnected) return;
        // Se refleja de inmediato; el estado real se confirma poco despues (el auricular tarda en arrancar).
        if (left) BeepLeft = on; else BeepRight = on;
        try
        {
            await _dev.BeepAsync(left, on);
            await Task.Delay(900);
            await RefreshBeepAsync();
        }
        catch (Exception ex) { Log.Write("Beep: " + ex.Message); }
    }

    public async Task RefreshBeepAsync()
    {
        if (!_dev.IsConnected) { BeepLeft = BeepRight = false; return; }
        try { ApplyBeeping(await _dev.GetBeepingAsync()); }
        catch (Exception ex) { Log.Write("Beep status: " + ex.Message); }
    }

    public async Task StopAllBeepsAsync()
    {
        if (!_dev.IsConnected) { BeepLeft = BeepRight = false; return; }
        try
        {
            await _dev.BeepAsync(true, false);
            await _dev.BeepAsync(false, false);
            ApplyBeeping(await _dev.GetBeepingAsync());
        }
        catch (Exception ex) { Log.Write("Beep stop: " + ex.Message); }
    }

    void ApplyBeeping((bool Left, bool Right)? b)
    {
        if (b is null) return;
        BeepLeft = b.Value.Left;
        BeepRight = b.Value.Right;
    }

    // ------------------------------------------------------------------ Check My Best Fit

    /// <summary>Ejecuta la prueba de sellado. null si fallo.</summary>
    public async Task<(bool Left, bool Right)?> RunSealTestAsync()
    {
        if (!_dev.IsConnected) return null;
        try { return await _dev.RunSealTestAsync(); }
        catch (Exception ex) { Log.Write("Seal: " + ex.Message); return null; }
        finally { QueueStatusRefresh(); }
    }

    // ------------------------------------------------------------------ ajustes

    bool _autoPause;
    public bool AutoPause
    {
        get => _autoPause;
        set { if (Set(ref _autoPause, value)) Send(() => _dev.SetAutoPlayPauseAsync(value)); }
    }

    bool _autoOffOn;
    public bool AutoOffOn
    {
        get => _autoOffOn;
        set { if (Set(ref _autoOffOn, value)) SendAutoOff(); }
    }

    int _autoOffMin = 30;
    public int AutoOffMinutes { get => _autoOffMin; private set => Set(ref _autoOffMin, value); }

    public void StepAutoOff(int dir)
    {
        int idx = Array.FindIndex(AutoOffSteps, s => s >= _autoOffMin);
        if (idx < 0) idx = AutoOffSteps.Length - 1;
        else if (dir < 0 || AutoOffSteps[idx] == _autoOffMin) idx += dir;
        idx = Util.Clamp(idx, 0, AutoOffSteps.Length - 1);
        if (AutoOffSteps[idx] == _autoOffMin) return;
        AutoOffMinutes = AutoOffSteps[idx];
        SendAutoOff();
    }

    void SendAutoOff()
    {
        bool on = _autoOffOn;
        int min = _autoOffMin;
        Send(() => _dev.SetAutoOffAsync(on, min));
    }

    /// <summary>Boton cerrar: 0 = preguntar, 1 = minimizar a la bandeja, 2 = salir.</summary>
    public int CloseAction
    {
        get => _settings.CloseAction;
        set { _settings.CloseAction = Util.Clamp(value, 0, 2); _settings.Save(); Raise(); }
    }

    public bool StartWithWindows
    {
        get => AppSettings.StartWithWindows;
        set { AppSettings.StartWithWindows = value; Raise(); }
    }

    // ------------------------------------------------------------------ ciclo de conexion

    public async void Start()
    {
        if (_running) return;
        _running = true;
        Audio.Start();
        while (_running)
        {
            if (!_dev.IsConnected)
            {
                State = ConnectionState.Searching;
                try
                {
                    await _dev.ConnectAsync(_settings.LastAddress);
                    if (_settings.LastAddress != _dev.Address)
                    {
                        _settings.LastAddress = _dev.Address;
                        _settings.Save();
                    }
                    _lastError = null;
                    _notice = null;
                    State = ConnectionState.Connected;
                    await RefreshAllAsync();
                }
                catch (Exception ex)
                {
                    Log.Write("Conexion fallida: " + ex.Message);
                    _dev.Disconnect();
                    _lastError = ex.Message;
                    State = ConnectionState.Disconnected;
                    Raise(nameof(StatusText));
                    await _wake.WaitAsync(6000);
                    continue;
                }
            }

            await _wake.WaitAsync(TimeSpan.FromSeconds(10));
            if (_running && _dev.IsConnected)
            {
                try
                {
                    ApplyBattery(await _dev.GetBatteryAsync());
                    _missedBeats = 0;
                }
                catch (Exception ex)
                {
                    if (++_missedBeats >= 2)
                    {
                        Log.Write("Los auriculares no responden: se da por desconectado (" + ex.Message + ")");
                        ForceDisconnect();
                    }
                }
            }
        }
    }

    public void Stop()
    {
        _running = false;
        Audio.Stop();
        _wake.Release();
        _dev.Dispose();
    }

    /// <summary>Fuerza una reconexion / refresco inmediato.</summary>
    public void Refresh()
    {
        if (_dev.IsConnected) _ = SafeRefreshAll();
        else _wake.Release();
    }

    int _missedBeats;

    void OnDisconnected()
    {
        if (!_running) return;
        ForceDisconnect();
    }

    /// <summary>Marca como desconectado y borra los datos en vivo para no mostrar informacion vieja.</summary>
    void ForceDisconnect()
    {
        _dev.Disconnect();
        _missedBeats = 0;
        State = ConnectionState.Disconnected;
        LeftBattery = RightBattery = CaseBattery = null;
        LeftCharging = RightCharging = CaseCharging = false;
        BeepLeft = BeepRight = false;
        InEarLeft = InEarRight = null;
        Raise(nameof(StatusText));
        _wake.Release();
    }

    // ------------------------------------------------------------------ conexion Bluetooth clasica

    BluetoothDevice? _classic;
    bool _classicWatching;

    /// <summary>
    /// Vigila la conexion Bluetooth "normal" de los auriculares (la que muestra Windows): al apagarlos o
    /// guardarlos en el estuche se desconecta al instante, mientras que la conexion BLE tarda en notarlo.
    /// </summary>
    async void WatchClassic(string mac)
    {
        if (_classicWatching) return;
        if (!ulong.TryParse(mac.Replace(":", ""), System.Globalization.NumberStyles.HexNumber, null, out var address)) return;
        _classicWatching = true;
        try
        {
            var open = Task.Run(async () => await BluetoothDevice.FromBluetoothAddressAsync(address));
            if (await Task.WhenAny(open, Task.Delay(8000)) != open) { _classicWatching = false; return; }
            _classic = await open;
            if (_classic is null) { _classicWatching = false; return; }
            _classic.ConnectionStatusChanged += (dev, _) =>
            {
                var st = dev.ConnectionStatus;
                _ui.InvokeAsync(() => OnClassicStatus(st));
            };
            Log.Write("Bluetooth clasico: " + _classic.ConnectionStatus);
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth clasico: " + ex.Message);
            _classicWatching = false;
        }
    }

    void OnClassicStatus(BluetoothConnectionStatus st)
    {
        Log.Write("Bluetooth clasico: " + st);
        if (st == BluetoothConnectionStatus.Disconnected)
        {
            // Durante la restauracion del audio la conexion clasica se reinicia a proposito.
            if (State != ConnectionState.Disconnected && !Audio.Restoring) ForceDisconnect();
        }
        else if (!_dev.IsConnected)
        {
            _wake.Release();   // vuelven: reconectar ya
        }
    }

    void OnUnsolicited(byte[] d)
    {
        if (d.Length < 2 || d[0] != Protocol.Header) return;
        switch (d[1])
        {
            case Protocol.Battery:
                ApplyBattery(Protocol.ParseBattery(d));
                break;
            case Protocol.RetDevStatus:
                var st = Protocol.ParseStatus(d);
                if (st != null) ApplyStatus(st);
                else if (Protocol.ParseInEar(d) is { } ie) ApplyInEar(ie);
                else if (d.Length > 3 && d[3] == Protocol.StatusSealing) { }
                else QueueStatusRefresh(); // aviso parcial (p.ej. modo cambiado con un gesto)
                break;
            case Protocol.RetBeeping:
                ApplyBeeping(Protocol.ParseBeeping(d));
                break;
            case Protocol.RetSmartSwitch:
                var sm = Protocol.ParseSmart(d);
                if (sm != SmartMode.Unknown && _smart != sm) { _smart = sm; Raise(nameof(Smart)); Raise(nameof(SmartDescription)); }
                break;
            case Protocol.GestureBatch when d.Length >= 6 && d[3] == 0x02:
                ApplyGestures(Protocol.ParseGestures(d));
                break;
        }
    }

    async void QueueStatusRefresh()
    {
        if (_statusRefreshQueued) return;
        _statusRefreshQueued = true;
        await Task.Delay(400);
        _statusRefreshQueued = false;
        try { ApplyStatus(await _dev.GetStatusAsync()); } catch { }
    }

    async Task SafeRefreshAll()
    {
        try { await RefreshAllAsync(); } catch { }
    }

    async Task RefreshAllAsync()
    {
        var info = await _dev.GetInfoAsync();
        Firmware = info.Firmware;
        Mac = info.Mac;
        WatchClassic(info.Mac);
        _colorId = info.ColorId;
        HasProductInfo = true;
        Raise(nameof(ColorName));
        Raise(nameof(ProductImage));
        ApplyStatus(await _dev.GetStatusAsync());
        ApplyBattery(await _dev.GetBatteryAsync());

        var sm = await _dev.GetSmartAsync();
        if (_smart != sm) { _smart = sm; Raise(nameof(Smart)); Raise(nameof(SmartDescription)); }
        try { ApplyInEar(await _dev.GetInEarAsync()); } catch { }

        try { ApplyGestures(await _dev.GetGesturesAsync()); }
        catch (Exception ex) { Log.Write("Gestos: " + ex.Message); }

        try
        {
            double[]? custom;
            try { custom = await _dev.ReadCustomEqAsync(); }
            catch (TimeoutException) { await Task.Delay(500); custom = await _dev.ReadCustomEqAsync(); }
            if (custom != null) CustomGains = custom;
            else if (_eqIndex >= 0 && _eqIndex < Protocol.EqPresets.Count)
                CustomGains = (double[])Protocol.EqPresets[_eqIndex].Gains.Clone();
        }
        catch (Exception ex) { Log.Write("EQ personalizado: " + ex.Message); }
    }

    void ApplyStatus(DeviceStatus? s)
    {
        if (s is null) return;
        SetMode(s.Mode);
        SetEq(Protocol.EqIndexOf(s.EqValue));
        if (s.AutoPlayPause is bool ap) Set(ref _autoPause, ap, nameof(AutoPause));
        if (s.AutoOffOn is bool ao) Set(ref _autoOffOn, ao, nameof(AutoOffOn));
        if (s.AutoOffMinutes is int m && m > 0) AutoOffMinutes = m;
    }

    void ApplyBattery(BatteryInfo? b)
    {
        if (b is null) return;
        BatteryLog.Add(b);
        LeftBattery = b.Left;
        RightBattery = b.Right;
        CaseBattery = b.Case;
        LeftCharging = b.LeftCharging;
        RightCharging = b.RightCharging;
        CaseCharging = b.CaseCharging;
    }

    async void Send(Func<Task> op, bool refreshStatus = true, [CallerMemberName] string? who = null)
    {
        Log.Write($"Send desde {who}, conectado={_dev.IsConnected}");
        if (!_dev.IsConnected) return;
        try
        {
            await op();
            if (!refreshStatus) return;
            await Task.Delay(350);
            ApplyStatus(await _dev.GetStatusAsync());
        }
        catch (Exception ex)
        {
            Log.Write("Error: " + ex);
            if (_dev.IsConnected)
            {
                _lastError = ex.Message;
                Raise(nameof(StatusText));
            }
        }
        finally
        {
            Raise(nameof(StatusText));
        }
    }

    // ------------------------------------------------------------------ INotifyPropertyChanged

    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
