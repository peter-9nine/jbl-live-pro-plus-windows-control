using System.Windows;

namespace JblControl;

public partial class App : Application
{
    const string MutexName = "LiveProPlusControl.SingleInstance";
    const string ShowEventName = "LiveProPlusControl.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    AppSettings _settings = new();
    DeviceController? _ctrl;
    TrayIcon? _tray;
    MainWindow? _window;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Una sola instancia: si ya esta abierta, se le pide que muestre su ventana.
        _mutex = new Mutex(true, MutexName, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.InvokeAsync(ShowMain);
        }) { IsBackground = true }.Start();

        _settings = AppSettings.Load();
        _ctrl = new DeviceController(Dispatcher, _settings);
        _window = new MainWindow(_ctrl);
        _tray = new TrayIcon(_ctrl, ShowMain, ShowBatteryUsage, RequestSmart, ExitApp);
        _ctrl.VolumeNoticeShown += text =>
        {
            // Si la ventana no esta a la vista, avisar tambien en la bandeja.
            if (_window is not { IsVisible: true, IsActive: true }) _tray?.ShowHint("Volume lowered", text);
        };

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowMain();

        _ctrl.Start();
    }

    public void ShowMain()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _ctrl?.Refresh();
    }

    /// <summary>La ventana se cierra a la bandeja; la primera vez se avisa.</summary>
    public void HideToTray()
    {
        _window?.Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            _settings.Save();
            _tray?.ShowHint("Live Pro+ Control is still running",
                "You will find it in the system tray. Right-click to change modes or exit.");
        }
    }

    BatteryUsageWindow? _usage;

    /// <summary>Ventana opcional con el uso de bateria de la sesion.</summary>
    public void ShowBatteryUsage()
    {
        if (_ctrl is null) return;
        if (_usage is null)
        {
            _usage = new BatteryUsageWindow(_ctrl.BatteryLog);
            _usage.Closed += (_, _) => _usage = null;
            if (_window is { IsVisible: true })
                _usage.Owner = _window;
            else
            {
                _usage.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _usage.ShowInTaskbar = true;
            }
            _usage.Show();
        }
        if (_usage.WindowState == WindowState.Minimized) _usage.WindowState = WindowState.Normal;
        _usage.Activate();
    }

    /// <summary>Cambio de Smart Audio desde la bandeja: siempre con la confirmacion de la ventana.</summary>
    void RequestSmart(SmartMode mode)
    {
        ShowMain();
        _window?.ShowPage("home");
        _window?.ConfirmSmart(mode);
    }

    public void ExitApp()
    {
        IsExiting = true;
        _ctrl?.Stop();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
