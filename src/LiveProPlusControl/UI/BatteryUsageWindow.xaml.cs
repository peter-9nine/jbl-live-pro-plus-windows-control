using System.Windows;
using System.Windows.Threading;

namespace LiveProPlusControl;

public partial class BatteryUsageWindow : Window
{
    readonly BatteryLog _log;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };

    public BatteryUsageWindow(BatteryLog log)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyling.RoundCorners(this);
        _log = log;
        DataContext = log;
        Chart.Log = log;
        _timer.Tick += (_, _) => _log.Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _log.Tick(); _timer.Start(); }
            else _timer.Stop();
        };
        Closed += (_, _) => { _timer.Stop(); Chart.Log = null; };
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Reset_Click(object sender, RoutedEventArgs e) => _log.Reset();
}
