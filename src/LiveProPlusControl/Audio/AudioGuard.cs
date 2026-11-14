using System.ComponentModel;
using System.Windows.Threading;

namespace LiveProPlusControl;

/// <summary>
/// Protege el oido y mantiene el audio de Windows.
///
/// Que pasa al cambiar Smart Audio &amp; Video: los auriculares reinician su enlace de audio y Windows
/// pierde la salida estereo (A2DP). Windows pasa entonces el sonido a la salida de llamadas
/// ("Hands-Free", mono y de baja calidad) a volumen 100 %: suena fuerte y distorsionado.
///
/// Este guardian:
///  - limita el volumen de cualquier salida activa de los auriculares (estereo o Hands-Free) mientras
///    el audio se esta recuperando y durante unos segundos despues de que vuelva la estereo
///    (los auriculares imponen su volumen guardado via AVRCP al reconectar);
///  - si la salida estereo no vuelve sola, desconecta todos los perfiles de audio y los reconecta
///    (lo mismo que desconectar/conectar a mano), con varios intentos;
///  - hace todas las llamadas de audio de Windows en otro hilo y con tiempo limite: el controlador
///    Bluetooth puede bloquear esas llamadas mientras reconfigura el audio.
/// </summary>
public sealed class AudioGuard : INotifyPropertyChanged
{
    const int MaxAttempts = 3;

    readonly AppSettings _settings;
    readonly DispatcherTimer _timer;
    bool _busy, _first = true;
    bool? _stereoActive;
    DateTime _capUntil;
    bool _notified;

    // restauracion tras cambiar el modo
    bool _restoring;
    DateTime _restoreStart, _stereoLostSince, _nextAttempt;
    int _attempts;
    bool _restoreFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? VolumeLimited;
    public event Action? AudioRestored;
    public event Action? RestoreFailed;

    public AudioGuard(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public bool Restoring => _restoring;
    public bool RestoreFailedFlag => _restoreFailed;

    public string AudioStatus =>
        _restoring ? "Reconnecting…" :
        _restoreFailed && _stereoActive != true ? "Not restored" :
        _stereoActive switch { true => "Connected", false => "Disconnected", _ => "Not paired" };

    public bool SafeVolumeEnabled
    {
        get => _settings.SafeVolumeEnabled;
        set { _settings.SafeVolumeEnabled = value; _settings.Save(); Raise(nameof(SafeVolumeEnabled)); }
    }

    public int SafeVolumeLevel => _settings.SafeVolumeLevel;

    public void StepLevel(int dir)
    {
        _settings.SafeVolumeLevel = Util.Clamp(_settings.SafeVolumeLevel + dir * 5, 5, 80);
        _settings.Save();
        Raise(nameof(SafeVolumeLevel));
    }

    /// <summary>Llamar justo despues de cambiar Smart Audio &amp; Video.</summary>
    public void BeginRestore()
    {
        _restoring = true;
        _restoreFailed = false;
        _restoreStart = DateTime.Now;
        _stereoLostSince = DateTime.MaxValue;
        _nextAttempt = DateTime.MinValue;
        _attempts = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        RaiseStatus();
    }

    /// <summary>Reintento manual (boton) si la restauracion automatica fallo.</summary>
    public void RetryRestore() => BeginRestore();

    /// <summary>Tras volver el audio, los auriculares imponen su volumen en ~1 s: se vigila solo este rato.</summary>
    static readonly TimeSpan CapWindow = TimeSpan.FromSeconds(4);

    void ArmCap(TimeSpan span)
    {
        if (!SafeVolumeEnabled) return;
        _capUntil = DateTime.Now + span;   // reemplaza (no alarga) la ventana anterior
        _notified = false;
    }

    async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await AudioWorker.Run(Snapshot, 3000);
            if (snap is null) return;   // Windows no respondio: se reintenta en el siguiente ciclo

            var stereo = snap.FirstOrDefault(e => !e.Endpoint.IsHandsFree);
            bool? active = stereo?.Endpoint.IsActive;

            // --- volumen seguro
            //  * salida estereo: solo durante la ventana corta tras volver (luego el usuario manda);
            //  * salida de llamadas (Hands-Free): mientras se restaura el audio, porque Windows la usa
            //    a volumen 100 % cuando falta la estereo.
            bool capStereo = DateTime.Now < _capUntil;
            bool capHandsFree = _restoring || capStereo;
            if (SafeVolumeEnabled && (capStereo || capHandsFree))
            {
                float level = _settings.SafeVolumeLevel / 100f;
                foreach (var e in snap.Where(e => e.Endpoint.IsActive && e.Volume > level + 0.005f &&
                                                  (e.Endpoint.IsHandsFree ? capHandsFree : capStereo)))
                {
                    var id = e.Endpoint.Id;
                    if (await AudioWorker.Run(() => AudioEndpoints.SetVolume(id, level), 3000))
                    {
                        Log.Write($"Volumen limitado ({e.Endpoint.Name}) {e.Volume:P0} -> {level:P0}");
                        if (!_notified) { _notified = true; VolumeLimited?.Invoke(_settings.SafeVolumeLevel); }
                    }
                }
            }

            // --- la salida estereo volvio
            bool cameBack = !_first && _stereoActive != true && active == true;
            _first = false;
            if (cameBack)
            {
                Log.Write("Audio estereo de Windows activo de nuevo");
                ArmCap(CapWindow);
                if (_restoring)
                {
                    _restoring = false;
                    AudioRestored?.Invoke();
                }
            }
            if (active != _stereoActive)
            {
                _stereoActive = active;
                RaiseStatus();
            }

            // --- restauracion tras el cambio de modo
            if (_restoring && stereo != null)
                await RestoreStepAsync(snap, active == true);

            var wanted = _restoring || DateTime.Now < _capUntil ? 150 : 800;
            if ((int)_timer.Interval.TotalMilliseconds != wanted)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(wanted);
                RaiseStatus();
            }
        }
        catch (Exception ex) { Log.Write("AudioGuard: " + ex.Message); }
        finally { _busy = false; }
    }

    async Task RestoreStepAsync(List<EndpointState> snap, bool stereoActive)
    {
        var now = DateTime.Now;
        if (stereoActive)
        {
            // puede tardar un momento en caerse tras el cambio; si sigue activa 12 s, no hacia falta nada
            if ((now - _restoreStart).TotalSeconds > 12)
            {
                _restoring = false;
                AudioRestored?.Invoke();
                RaiseStatus();
            }
            _stereoLostSince = DateTime.MaxValue;
            return;
        }

        if (_stereoLostSince == DateTime.MaxValue) _stereoLostSince = now;

        // Dar 3 s a Windows para que la recupere solo; despues, desconectar todo y reconectar.
        if ((now - _stereoLostSince).TotalSeconds < 3 || now < _nextAttempt) return;

        if (_attempts >= MaxAttempts)
        {
            _restoring = false;
            _restoreFailed = true;
            Log.Write("No se pudo restaurar el audio de Windows");
            RestoreFailed?.Invoke();
            RaiseStatus();
            return;
        }

        _attempts++;
        Log.Write($"Restaurando audio de Windows (intento {_attempts})");
        var ids = snap.Select(e => e.Endpoint.Id).ToList();
        var stereoId = snap.First(e => !e.Endpoint.IsHandsFree).Endpoint.Id;
        await AudioWorker.Run(() => { foreach (var id in ids) AudioEndpoints.Disconnect(id); return true; }, 5000);
        await Task.Delay(2000);
        await AudioWorker.Run(() => AudioEndpoints.Reconnect(stereoId), 5000);
        _nextAttempt = DateTime.Now.AddSeconds(25);   // Windows tarda 10-20 s en restablecer el perfil
    }

    sealed record EndpointState(AudioEndpoints.Endpoint Endpoint, float? Volume);

    /// <summary>Estado de las salidas; el volumen solo se lee de las activas.</summary>
    static List<EndpointState> Snapshot() =>
        AudioEndpoints.Find()
            .Select(e => new EndpointState(e, e.IsActive ? AudioEndpoints.GetVolume(e.Id) : null))
            .ToList();

    void RaiseStatus()
    {
        Raise(nameof(Restoring));
        Raise(nameof(RestoreFailedFlag));
        Raise(nameof(AudioStatus));
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Ejecuta llamadas de audio de Windows en otro hilo, con tiempo limite.</summary>
static class AudioWorker
{
    public static async Task<T?> Run<T>(Func<T> f, int timeoutMs)
    {
        var t = Task.Factory.StartNew(f, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t)
        {
            Log.Write("Windows no respondio a una llamada de audio (se abandona)");
            _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return default;
        }
        try { return await t; }
        catch (Exception ex) { Log.Write("Audio: " + ex.Message); return default; }
    }
}
