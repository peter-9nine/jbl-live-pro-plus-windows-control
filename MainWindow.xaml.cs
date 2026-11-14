using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JblControl;

public partial class MainWindow : Window
{
    readonly DeviceController _ctrl;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    SmartMode _pendingSmart;
    bool _fitRunning;

    public MainWindow(DeviceController ctrl)
    {
        InitializeComponent();
        _ctrl = ctrl;
        DataContext = ctrl;
        EqEditor.Gains = (double[])ctrl.CustomGains.Clone();
        ctrl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceController.CustomGains))
                EqEditor.Gains = (double[])ctrl.CustomGains.Clone();
            if (e.PropertyName == nameof(DeviceController.IsConnected) && !ctrl.IsConnected && Overlay.IsVisible && !_fitRunning)
                CloseOverlay();
        };

        // Sondeo mientras haga falta: estado del pitido (Find My Buds) y si estan puestos (Gestos / Best Fit).
        _poll.Tick += async (_, _) =>
        {
            if (!IsVisible || !_ctrl.IsConnected) return;
            if (FindPanel.IsVisible) await _ctrl.RefreshBeepAsync();
            else if (PageGestures.IsVisible || (FitPanel.IsVisible && !_fitRunning)) await _ctrl.RefreshInEarAsync();
        };
        _poll.Start();

        SourceInitialized += (_, _) => WindowStyling.RoundCorners(this);
    }

    static App CurrentApp => (App)Application.Current;

    // ------------------------------------------------------------------ ventana

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    /// <summary>Boton cerrar / Alt+F4: segun el ajuste, pregunta, va a la bandeja o sale.</summary>
    void RequestClose()
    {
        switch (_ctrl.CloseAction)
        {
            case 1: CurrentApp.HideToTray(); break;
            case 2: CurrentApp.ExitApp(); break;
            default:
                if (!IsVisible) { CurrentApp.HideToTray(); break; }
                CloseRemember.IsChecked = false;
                ShowDialog(ClosePanel);
                break;
        }
    }

    void CloseTray_Click(object sender, RoutedEventArgs e)
    {
        if (CloseRemember.IsChecked == true) _ctrl.CloseAction = 1;
        Overlay.Visibility = Visibility.Collapsed;
        CurrentApp.HideToTray();
    }

    void CloseExit_Click(object sender, RoutedEventArgs e)
    {
        if (CloseRemember.IsChecked == true) _ctrl.CloseAction = 2;
        CurrentApp.ExitApp();
    }
    void Retry_Click(object sender, RoutedEventArgs e) => _ctrl.Refresh();
    void BatteryUsage_Click(object sender, RoutedEventArgs e) => CurrentApp.ShowBatteryUsage();
    void AutoOffMinus_Click(object sender, RoutedEventArgs e) => _ctrl.StepAutoOff(-1);
    void AutoOffPlus_Click(object sender, RoutedEventArgs e) => _ctrl.StepAutoOff(+1);
    void SafeVolMinus_Click(object sender, RoutedEventArgs e) => _ctrl.Audio.StepLevel(-1);
    void AudioRetry_Click(object sender, RoutedEventArgs e) => _ctrl.Audio.RetryRestore();

    void BtSettings_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Bluetooth settings: " + ex.Message); }
    }
    void SafeVolPlus_Click(object sender, RoutedEventArgs e) => _ctrl.Audio.StepLevel(+1);

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 / cerrar: se oculta en la bandeja salvo que se este saliendo de la app.
        if (!CurrentApp.IsExiting)
        {
            e.Cancel = true;
            RequestClose();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ navegacion

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome is null) return; // durante InitializeComponent
        PageHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEq.Visibility = TabEq.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGestures.Visibility = TabGestures.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (PageGestures.IsVisible) _ = _ctrl.RefreshInEarAsync();
    }

    public void ShowPage(string name)
    {
        (name switch
        {
            "eq" => TabEq,
            "gestures" => TabGestures,
            "settings" => TabSettings,
            _ => TabHome,
        }).IsChecked = true;
    }

    // ------------------------------------------------------------------ Smart Audio & Video

    void Smart_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        // El modo no cambia al hacer clic: primero se explica lo que va a pasar.
        e.Handled = true;
        var mode = (SmartMode)Enum.Parse(typeof(SmartMode), (string)((FrameworkElement)sender).Tag);
        ConfirmSmart(mode);
    }

    /// <summary>Pide confirmacion para cambiar el modo (tambien desde la bandeja).</summary>
    public void ConfirmSmart(SmartMode mode)
    {
        if (!_ctrl.IsConnected || mode == _ctrl.Smart) return;
        _pendingSmart = mode;
        string name = mode switch { SmartMode.Audio => "Audio Mode", SmartMode.Video => "Video Mode", _ => "Normal" };
        SmartConfirmTitle.Text = $"Switch to {name}?";
        SmartConfirmVolume.Text = _ctrl.Audio.SafeVolumeEnabled
            ? $"To protect your hearing, the volume will be set to {_ctrl.Audio.SafeVolumeLevel}% when the audio comes back. Pause playback before continuing."
            : "Safe volume is turned off: the earbuds may come back at a high volume. Lower the volume and pause playback before continuing.";
        ShowDialog(SmartConfirm);
    }

    void SmartConfirm_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay();
        _ctrl.ChangeSmart(_pendingSmart);
    }

    // ------------------------------------------------------------------ ecualizador

    void EditEq_Click(object sender, RoutedEventArgs e) => ShowPage("eq");

    void EqEditor_Committed(double[] gains)
    {
        _ctrl.ApplyCustomEq(gains);
        FlashSaved();
    }

    void EqStartFrom_Click(object sender, RoutedEventArgs e)
    {
        int i = int.Parse((string)((Button)sender).Tag);
        var g = (double[])Protocol.EqPresets[i].Gains.Clone();
        EqEditor.Gains = g;
        _ctrl.ApplyCustomEq(g);
        FlashSaved();
    }

    void EqUseCustom_Click(object sender, RoutedEventArgs e)
    {
        _ctrl.ApplyCustomEq(EqEditor.Gains);
        FlashSaved();
    }

    void FlashSaved()
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        EqSavedText.BeginAnimation(OpacityProperty, a);
    }

    // ------------------------------------------------------------------ dialogos

    void ShowDialog(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { ClosePanel, SmartConfirm, FindWarn, FindPanel, FitPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    async void CloseOverlay()
    {
        bool wasFinding = FindPanel.IsVisible;
        Overlay.Visibility = Visibility.Collapsed;
        if (wasFinding) await _ctrl.StopAllBeepsAsync();
    }

    void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    // Find My Buds
    void FindBuds_Click(object sender, RoutedEventArgs e) => ShowDialog(FindWarn);

    async void FindContinue_Click(object sender, RoutedEventArgs e)
    {
        ShowDialog(FindPanel);
        await _ctrl.RefreshBeepAsync();
    }

    async void BeepLeft_Click(object sender, RoutedEventArgs e) => await _ctrl.SetBeepAsync(left: true, on: !_ctrl.BeepLeft);
    async void BeepRight_Click(object sender, RoutedEventArgs e) => await _ctrl.SetBeepAsync(left: false, on: !_ctrl.BeepRight);
    async void StopBeeps_Click(object sender, RoutedEventArgs e) => await _ctrl.StopAllBeepsAsync();

    // Check My Best Fit (estados de CheckMyBestFitFragment: inicio, midiendo, resultado)

    enum FitMode { Start, Testing, Result }
    FitMode _fitMode;

    async void BestFit_Click(object sender, RoutedEventArgs e)
    {
        SetFitStart();
        ShowDialog(FitPanel);
        await _ctrl.RefreshInEarAsync();
    }

    void SetFitImage(string? side, double opacity)
    {
        FitImage.Source = new BitmapImage(new Uri(_ctrl.ProductImageVariant(side)));
        FitImage.Opacity = opacity;
    }

    void SetRing(bool left, bool right)
    {
        FitRingFull.Visibility = left && right ? Visibility.Visible : Visibility.Collapsed;
        FitRingLeft.Visibility = left && !right ? Visibility.Visible : Visibility.Collapsed;
        FitRingRight.Visibility = right && !left ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetSpinner(bool on)
    {
        FitSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FitSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
            on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    void ShowHow(bool how)
    {
        FitHowArea.Visibility = how ? Visibility.Visible : Visibility.Collapsed;
        FitCircleArea.Visibility = FitLabels.Visibility = how ? Visibility.Collapsed : Visibility.Visible;
        FitHowText.Text = how ? "Back to the test" : "Show me how";
    }

    void FitHow_Click(object sender, RoutedEventArgs e) => ShowHow(FitHowArea.Visibility != Visibility.Visible);

    void SetFitStart()
    {
        _fitMode = FitMode.Start;
        ShowHow(false);
        FitHowLink.Visibility = Visibility.Visible;
        SetFitImage(null, 1);
        SetRing(false, false);
        SetSpinner(false);
        FitText.Text = "For your best listening experience, check the fit of your ear tips. Turn the earbuds when slotted in your ears to find the most optimal wear position. The earbuds should be worn when testing.";
        FitTesting.Visibility = Visibility.Collapsed;
        FitWearHint.SetBinding(VisibilityProperty, new Binding(nameof(DeviceController.BothInEar)) { Converter = (IValueConverter)FindResource("InvBoolToVis") });
        FitButtons.Visibility = Visibility.Visible;
        FitCancelBtn.Visibility = Visibility.Visible;
        FitCancelBtn.Content = "Cancel";
        FitStartBtn.Content = "Start";
        FitStartBtn.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(DeviceController.BothInEar)));
    }

    async void FitStart_Click(object sender, RoutedEventArgs e)
    {
        if (_fitMode == FitMode.Testing) return;
        if (_fitMode == FitMode.Result && (string)FitStartBtn.Content == "Done") { CloseOverlay(); return; }

        _fitMode = FitMode.Testing;
        _fitRunning = true;
        try
        {
            ShowHow(false);
            FitHowLink.Visibility = Visibility.Collapsed;
            SetFitImage(null, 1);
            SetRing(false, false);
            SetSpinner(true);
            FitText.Text = "Please do not move your earbuds during the test";
            FitTesting.Visibility = Visibility.Visible;
            BindingOperations.ClearBinding(FitWearHint, VisibilityProperty);
            FitWearHint.Visibility = Visibility.Collapsed;
            FitButtons.Visibility = Visibility.Hidden;

            var r = await _ctrl.RunSealTestAsync();
            SetSpinner(false);
            FitTesting.Visibility = Visibility.Collapsed;
            if (!Overlay.IsVisible) return;

            _fitMode = FitMode.Result;
            FitButtons.Visibility = Visibility.Visible;
            BindingOperations.ClearBinding(FitStartBtn, IsEnabledProperty);
            FitStartBtn.IsEnabled = true;
            FitCancelBtn.Content = "Done";

            if (r is null)
            {
                SetFitImage(null, 0.5);
                FitText.Text = "The earbuds did not respond. Check that they are connected and worn, then try again.";
                FitStartBtn.Content = "Retry";
                return;
            }

            var (l, rr) = r.Value;
            SetRing(l, rr);
            switch (l, rr)
            {
                case (true, true):
                    SetFitImage(null, 1);
                    FitText.Text = "Your ear tips are a good fit for both ears.";
                    FitCancelBtn.Visibility = Visibility.Collapsed;
                    FitStartBtn.Content = "Done";
                    break;
                case (true, false):
                    SetFitImage("left", 1);
                    FitText.Text = "The left ear tip is a good fit. Please adjust your right earbud, or change the ear tip size that is most comfortable in your ear and try the test again.";
                    FitStartBtn.Content = "Retry";
                    break;
                case (false, true):
                    SetFitImage("right", 1);
                    FitText.Text = "The right ear tip is a good fit. Please adjust your left earbud, or change the ear tip size that is most comfortable in your ear and try the test again.";
                    FitStartBtn.Content = "Retry";
                    break;
                default:
                    SetFitImage(null, 0.5);
                    FitText.Text = "Please adjust both sides or change ear tip and then try again.";
                    FitStartBtn.Content = "Retry";
                    break;
            }
        }
        finally
        {
            _fitRunning = false;
        }
    }
}
