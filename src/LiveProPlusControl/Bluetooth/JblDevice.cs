using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace LiveProPlusControl;

/// <summary>Conexion BLE a los audifonos usando la API WinRT de Windows.</summary>
public sealed class JblDevice : IDisposable
{
    static readonly Guid ServiceUuid = Guid.Parse("65786365-6c70-6f69-6e74-2e636f6d0000");
    static readonly Guid RxUuid = Guid.Parse("65786365-6c70-6f69-6e74-2e636f6d0001"); // notify
    static readonly Guid TxUuid = Guid.Parse("65786365-6c70-6f69-6e74-2e636f6d0002"); // write

    public const string NamePrefix = "JBL LIVE PRO+";

    sealed class Waiter(Func<byte[], bool> match)
    {
        public Func<byte[], bool> Match { get; } = match;
        public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly List<Waiter> _waiters = [];
    readonly object _waitGate = new();
    readonly SemaphoreSlim _lock = new(1, 1);

    BluetoothLEDevice? _device;
    GattSession? _session;
    GattDeviceService? _service;
    GattCharacteristic? _rx, _tx;

    /// <summary>Paquetes que llegan sin que los hayamos pedido (p.ej. cambios hechos con los gestos).</summary>
    public event Action<byte[]>? Unsolicited;
    public event Action? Disconnected;

    public ulong Address { get; private set; }

    public bool IsConnected =>
        _tx != null && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>Intenta primero la direccion conocida y si falla busca por anuncio BLE.</summary>
    public async Task ConnectAsync(ulong? knownAddress, int scanTimeoutMs = 10000)
    {
        Close();
        if (knownAddress is ulong known && known != 0)
        {
            try
            {
                await OpenAsync(known);
                return;
            }
            catch
            {
                Close();
            }
        }
        await OpenAsync(await FindAsync(NamePrefix, scanTimeoutMs));
    }

    async Task OpenAsync(ulong address)
    {
        var open = Task.Run(() => OpenCoreAsync(address));
        if (await Task.WhenAny(open, Task.Delay(20000)) != open)
        {
            _ = open.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("La conexion BLE no respondio.");
        }
        await open;
    }

    async Task OpenCoreAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new InvalidOperationException("No se pudo abrir el dispositivo BLE.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"No se encontro el servicio de control ({services.Status}).");
        _service = services.Services[0];

        _rx = await GetCharacteristic(_service, RxUuid);
        _tx = await GetCharacteristic(_service, TxUuid);

        _rx.ValueChanged += OnValueChanged;
        var cccd = await _rx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"No se pudieron activar las notificaciones ({cccd}).");

        Address = address;
    }

    static async Task<GattCharacteristic> GetCharacteristic(GattDeviceService svc, Guid uuid)
    {
        var r = await svc.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            throw new InvalidOperationException($"No se encontro la caracteristica {uuid} ({r.Status}).");
        return r.Characteristics[0];
    }

    /// <summary>Busca el anuncio BLE cuyo nombre empiece con el prefijo (p.ej. "JBL LIVE PRO+ TWS-LE").</summary>
    static async Task<ulong> FindAsync(string prefix, int timeoutMs)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                found.TrySetResult(e.BluetoothAddress);
        };
        watcher.Start();
        try
        {
            var winner = await Task.WhenAny(found.Task, Task.Delay(timeoutMs));
            if (winner != found.Task)
                throw new TimeoutException("No se encontraron los audifonos.");
            return await found.Task;
        }
        finally
        {
            watcher.Stop();
        }
    }

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
        if (data is null || data.Length < 2) return;
        Log.Write("<- " + Log.Hex(data));

        Waiter? hit = null;
        lock (_waitGate)
        {
            hit = _waiters.FirstOrDefault(w => w.Match(data));
            if (hit != null) _waiters.Remove(hit);
        }
        if (hit != null) hit.Tcs.TrySetResult(data);
        else Unsolicited?.Invoke(data);
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            Disconnected?.Invoke();
    }

    async Task WriteRaw(byte[] packet)
    {
        if (_tx is null) throw new InvalidOperationException("No conectado.");
        Log.Write("-> " + Log.Hex(packet));
        // Con tiempo limite: si los auriculares reinician el enlace, la escritura puede no completarse nunca
        // y dejaria la app bloqueada.
        var tx = _tx;
        var write = Task.Run(async () => await tx.WriteValueWithResultAsync(
            CryptographicBuffer.CreateFromByteArray(packet), GattWriteOption.WriteWithResponse));
        if (await Task.WhenAny(write, Task.Delay(4000)) != write)
        {
            Log.Write("Escritura sin respuesta: se fuerza la reconexion");
            _ = write.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            Disconnected?.Invoke();
            throw new TimeoutException("Los auriculares no confirmaron el comando.");
        }
        var res = await write;
        if (res.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Error al escribir ({res.Status}).");
    }

    Waiter AddWaiter(Func<byte[], bool> match)
    {
        var w = new Waiter(match);
        lock (_waitGate) _waiters.Add(w);
        return w;
    }

    void RemoveWaiter(Waiter w)
    {
        lock (_waitGate) _waiters.Remove(w);
    }

    async Task<byte[]> WaitFor(Waiter w, int timeoutMs, string what)
    {
        var winner = await Task.WhenAny(w.Tcs.Task, Task.Delay(timeoutMs));
        RemoveWaiter(w);
        if (winner != w.Tcs.Task) throw new TimeoutException($"Los audifonos no respondieron ({what}).");
        return await w.Tcs.Task;
    }

    static Func<byte[], bool> Cmd(byte cmd) => d => d[0] == Protocol.Header && d[1] == cmd;

    public async Task SendAsync(params byte[][] packets)
    {
        await _lock.WaitAsync();
        try
        {
            for (int i = 0; i < packets.Length; i++)
            {
                if (i > 0) await Task.Delay(150);
                await WriteRaw(packets[i]);
            }
        }
        finally { _lock.Release(); }
    }

    public Task<byte[]> RequestAsync(byte[] packet, byte replyCmd, int timeoutMs = 3000) =>
        RequestAsync(packet, Cmd(replyCmd), timeoutMs);

    public async Task<byte[]> RequestAsync(byte[] packet, Func<byte[], bool> match, int timeoutMs)
    {
        await _lock.WaitAsync();
        try
        {
            var w = AddWaiter(match);
            await WriteRaw(packet);
            return await WaitFor(w, timeoutMs, Log.Hex(packet));
        }
        finally { _lock.Release(); }
    }

    // ---- API de alto nivel ----
    public async Task<DeviceInfo> GetInfoAsync() =>
        Protocol.ParseInfo(await RequestAsync(Protocol.QueryInfo(), Protocol.RetDevInfo));

    /// <summary>Solo acepta el estado completo (sub 0x30), no los avisos parciales.</summary>
    public async Task<DeviceStatus?> GetStatusAsync() =>
        Protocol.ParseStatus(await RequestAsync(Protocol.QueryStatus(),
            d => d[0] == Protocol.Header && d[1] == Protocol.RetDevStatus && d.Length > 3 && d[3] == Protocol.StatusAll, 3000));

    public async Task<BatteryInfo?> GetBatteryAsync() =>
        Protocol.ParseBattery(await RequestAsync(Protocol.QueryBattery(), Protocol.Battery));

    public async Task<SmartMode> GetSmartAsync() =>
        Protocol.ParseSmart(await RequestAsync(Protocol.QuerySmart(), Protocol.RetSmartSwitch));

    public async Task<Dictionary<byte, byte>?> GetGesturesAsync() =>
        Protocol.ParseGestures(await RequestAsync(Protocol.QueryGestures(),
            d => d[0] == Protocol.Header && d[1] == Protocol.GestureBatch && d.Length > 3 && d[3] == 0x02, 3000));

    public async Task<(bool Left, bool Right)?> GetInEarAsync() =>
        Protocol.ParseInEar(await RequestAsync(Protocol.QueryInEar(),
            d => d[0] == Protocol.Header && d[1] == Protocol.RetDevStatus && d.Length >= 6 && d[3] == Protocol.StatusInEar, 3000));

    public async Task<(bool Left, bool Right)?> GetBeepingAsync() =>
        Protocol.ParseBeeping(await RequestAsync(Protocol.QueryBeeping(), Protocol.RetBeeping));

    /// <summary>
    /// Check My Best Fit: los auriculares reproducen un tono, pasan un momento a Ambient Aware,
    /// miden el sellado y responden AA 22 03 42 L R.
    /// </summary>
    public async Task<(bool Left, bool Right)?> RunSealTestAsync() =>
        Protocol.ParseSealing(await RequestAsync(Protocol.QuerySealing(),
            d => d[0] == Protocol.Header && d[1] == Protocol.RetDevStatus && d.Length >= 6 && d[3] == Protocol.StatusSealing, 20000));

    public Task SetModeAsync(NoiseMode mode) => SendAsync(Protocol.SetMode(mode));
    public Task SetEqAsync(byte preset) => SendAsync(Protocol.SetEq(preset));
    public Task SetAutoOffAsync(bool on, int minutes) => SendAsync(Protocol.AutoOff(on, minutes));
    public Task SetAutoPlayPauseAsync(bool on) => SendAsync(Protocol.AutoPlayPause(on));
    public Task SetSmartAsync(SmartMode mode) => SendAsync(Protocol.SetSmart(mode));
    public Task SetGestureAsync(byte gesture, byte action) => SendAsync(Protocol.SetGesture(gesture, action));
    public Task BeepAsync(bool left, bool start) => SendAsync(Protocol.Beep(left, start));

    /// <summary>Escribe el ecualizador personalizado esperando el ACK (AA 00 02 41 00) de cada parte.</summary>
    public async Task WriteCustomEqAsync(double[] gains)
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var pkt in Protocol.CustomEqPackets(gains))
            {
                var w = AddWaiter(d => d[0] == Protocol.Header && d[1] == Protocol.DevAck && d.Length >= 4 && d[3] == Protocol.SetCustomEq);
                await WriteRaw(pkt);
                var ack = await WaitFor(w, 2000, "ecualizador");
                if (ack.Length >= 5 && ack[4] != 0)
                    throw new InvalidOperationException("Los auriculares rechazaron el ecualizador.");
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Lee el ecualizador personalizado guardado (AA 42 01 01). Los datos llegan en trozos sin cabecera
    /// y hay que confirmar cada uno con AA 01 02 43 00; termina con AA 04 43.
    /// </summary>
    public async Task<double[]?> ReadCustomEqAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var first = AddWaiter(d => d[0] == Protocol.Header &&
                                       (d[1] == Protocol.RetCustomEq || (d[1] == Protocol.DevFinAck && d.Length >= 3 && d[2] == Protocol.RetCustomEq)));
            await WriteRaw(Protocol.Packet(Protocol.ReqCustomEq, 0x01));
            var head = await WaitFor(first, 3000, "lectura de ecualizador");
            if (head[1] != Protocol.RetCustomEq) return null;   // no hay ecualizador personalizado

            var data = new List<byte>();
            byte[] ack = Protocol.Packet(Protocol.AppAck, Protocol.RetCustomEq, 0x00);
            for (int guard = 0; guard < 10; guard++)
            {
                var next = AddWaiter(d => d[0] != Protocol.Header ||
                                          (d[1] == Protocol.DevFinAck && d.Length >= 3 && d[2] == Protocol.RetCustomEq));
                await WriteRaw(ack);
                var pkt = await WaitFor(next, 3000, "trozo de ecualizador");
                if (pkt[0] == Protocol.Header) break;
                data.AddRange(pkt.Skip(1));
            }
            return Protocol.ParseCustomEq(data.ToArray());
        }
        finally { _lock.Release(); }
    }

    void Close()
    {
        lock (_waitGate)
        {
            foreach (var w in _waiters) w.Tcs.TrySetCanceled();
            _waiters.Clear();
        }
        var rx = _rx;
        var device = _device;
        var service = _service;
        var session = _session;
        _rx = _tx = null;
        _service = null;
        _session = null;
        _device = null;
        if (rx is null && device is null && service is null && session is null) return;
        Task.Run(() =>
        {
            try
            {
                if (rx != null) rx.ValueChanged -= OnValueChanged;
                if (device != null) device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                service?.Dispose();
                session?.Dispose();
                device?.Dispose();
            }
            catch (Exception ex) { Log.Write("Close: " + ex.Message); }
        });
    }

    public void Disconnect() => Close();
    public void Dispose() => Close();
}
