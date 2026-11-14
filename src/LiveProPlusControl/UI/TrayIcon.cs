using System.ComponentModel;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace LiveProPlusControl;

/// <summary>Icono en la bandeja del sistema con menu de acceso rapido.</summary>
public sealed class TrayIcon : IDisposable
{
    static readonly (string Text, NoiseMode Mode)[] Modes =
    [
        ("Off", NoiseMode.Off),
        ("Noise Cancelling", NoiseMode.Anc),
        ("Ambient Aware", NoiseMode.Ambient),
    ];

    static readonly (string Text, SmartMode Mode)[] SmartModes =
    [
        ("Normal", SmartMode.Normal),
        ("Audio Mode", SmartMode.Audio),
        ("Video Mode", SmartMode.Video),
    ];

    readonly DeviceController _ctrl;
    readonly WF.NotifyIcon _icon;
    readonly WF.ToolStripMenuItem _header, _battery, _eqMenu, _smartMenu;
    readonly List<WF.ToolStripMenuItem> _smartItems = [];
    readonly List<WF.ToolStripMenuItem> _modeItems = [];
    readonly List<WF.ToolStripMenuItem> _eqItems = [];

    public TrayIcon(DeviceController ctrl, Action open, Action batteryUsage, Action<SmartMode> smart, Action exit)
    {
        _ctrl = ctrl;

        var menu = new WF.ContextMenuStrip
        {
            Font = new SD.Font("Segoe UI", 9.5f),
            Renderer = new WF.ToolStripProfessionalRenderer(new MenuColors()),
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };

        _header = new WF.ToolStripMenuItem(ctrl.DeviceName) { Enabled = false, Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) };
        _battery = new WF.ToolStripMenuItem("--") { Enabled = false };
        menu.Items.Add(_header);
        menu.Items.Add(_battery);
        menu.Items.Add(new WF.ToolStripSeparator());

        foreach (var (text, mode) in Modes)
        {
            var item = new WF.ToolStripMenuItem(text) { Tag = mode };
            item.Click += (_, _) => _ctrl.Mode = mode;
            _modeItems.Add(item);
            menu.Items.Add(item);
        }
        menu.Items.Add(new WF.ToolStripSeparator());

        _eqMenu = new WF.ToolStripMenuItem("Equalizer");
        if (_eqMenu.DropDown is WF.ToolStripDropDownMenu dd)
        {
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
            dd.Renderer = menu.Renderer;
        }
        for (int i = 0; i <= Protocol.EqCustomIndex; i++)
        {
            int idx = i;
            var item = new WF.ToolStripMenuItem(i == Protocol.EqCustomIndex ? "Custom" : Protocol.EqPresets[i].Name);
            item.Click += (_, _) => _ctrl.EqIndex = idx;
            _eqItems.Add(item);
            _eqMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_eqMenu);

        _smartMenu = new WF.ToolStripMenuItem("Smart Audio & Video");
        if (_smartMenu.DropDown is WF.ToolStripDropDownMenu sd)
        {
            sd.ShowImageMargin = false;
            sd.ShowCheckMargin = true;
            sd.Renderer = menu.Renderer;
        }
        foreach (var (text, mode) in SmartModes)
        {
            var item = new WF.ToolStripMenuItem(text) { Tag = mode };
            item.Click += (_, _) => smart(mode);
            _smartItems.Add(item);
            _smartMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_smartMenu);
        var usageItem = new WF.ToolStripMenuItem("Battery usage…");
        usageItem.Click += (_, _) => batteryUsage();
        menu.Items.Add(usageItem);
        menu.Items.Add(new WF.ToolStripSeparator());

        var openItem = new WF.ToolStripMenuItem("Open Live Pro+ Control") { Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);
        var exitItem = new WF.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(exitItem);

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) open(); };

        _ctrl.PropertyChanged += OnChanged;
        Update();
    }

    static SD.Icon LoadIcon()
    {
        var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var s = res.Stream;
        return new SD.Icon(s, WF.SystemInformation.SmallIconSize);
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None);

    void OnChanged(object? s, PropertyChangedEventArgs e) => Update();

    static string Pct(int? v) => v is int i ? $"{i}%" : "--";

    void Update()
    {
        bool on = _ctrl.IsConnected;
        string batt = $"L {Pct(_ctrl.LeftBattery)}   R {Pct(_ctrl.RightBattery)}"
                      + (_ctrl.CaseBattery is null ? "" : $"   Case {Pct(_ctrl.CaseBattery)}");
        _battery.Text = on ? batt : _ctrl.StatusText;

        foreach (var item in _modeItems)
        {
            item.Enabled = on;
            item.Checked = on && (NoiseMode)item.Tag! == _ctrl.Mode;
        }
        _eqMenu.Enabled = on;
        _smartMenu.Enabled = on;
        foreach (var item in _smartItems)
            item.Checked = (SmartMode)item.Tag! == _ctrl.Smart;
        for (int i = 0; i < _eqItems.Count; i++)
            _eqItems[i].Checked = i == _ctrl.EqIndex;

        string mode = _ctrl.Mode switch
        {
            NoiseMode.Anc => "ANC",
            NoiseMode.Ambient => "Ambient Aware",
            NoiseMode.TalkThru => "TalkThru",
            _ => "Off",
        };
        string tip = on
            ? $"JBL LIVE PRO+ · {mode}\nL {Pct(_ctrl.LeftBattery)} · R {Pct(_ctrl.RightBattery)}"
              + (_ctrl.CaseBattery is null ? "" : $" · Case {Pct(_ctrl.CaseBattery)}")
            : $"JBL LIVE PRO+\n{_ctrl.StatusText}";
        _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // .NET Framework limita a 63
    }

    public void Dispose()
    {
        _ctrl.PropertyChanged -= OnChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class MenuColors : WF.ProfessionalColorTable
    {
        static readonly SD.Color Hover = SD.Color.FromArgb(0xFF, 0xF0, 0xE6);
        static readonly SD.Color Accent = SD.Color.FromArgb(0xFF, 0x59, 0x01);
        static readonly SD.Color Line = SD.Color.FromArgb(0xE5, 0xEA, 0xEF);

        public override SD.Color MenuItemSelected => Hover;
        public override SD.Color MenuItemBorder => Hover;
        public override SD.Color MenuItemSelectedGradientBegin => Hover;
        public override SD.Color MenuItemSelectedGradientEnd => Hover;
        public override SD.Color MenuItemPressedGradientBegin => Hover;
        public override SD.Color MenuItemPressedGradientEnd => Hover;
        public override SD.Color MenuBorder => Line;
        public override SD.Color ToolStripDropDownBackground => SD.Color.White;
        public override SD.Color ImageMarginGradientBegin => SD.Color.White;
        public override SD.Color ImageMarginGradientMiddle => SD.Color.White;
        public override SD.Color ImageMarginGradientEnd => SD.Color.White;
        public override SD.Color SeparatorDark => Line;
        public override SD.Color SeparatorLight => SD.Color.White;
        public override SD.Color CheckBackground => Hover;
        public override SD.Color CheckSelectedBackground => Hover;
        public override SD.Color CheckPressedBackground => Hover;
        public override SD.Color ButtonSelectedBorder => Accent;
    }
}
