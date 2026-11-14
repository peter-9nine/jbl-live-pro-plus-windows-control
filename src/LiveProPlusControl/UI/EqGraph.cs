using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiveProPlusControl;

/// <summary>
/// Curva del ecualizador de 10 bandas (estilo de la app de JBL).
/// Con <see cref="Editable"/> los puntos se arrastran verticalmente (pasos de 0.5 dB).
/// </summary>
public sealed class EqGraph : FrameworkElement
{
    const double LabelHeight = 18;
    const double PadX = 16;

    public static readonly DependencyProperty GainsProperty = DependencyProperty.Register(
        nameof(Gains), typeof(double[]), typeof(EqGraph),
        new FrameworkPropertyMetadata(new double[10], FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(EqGraph),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x59, 0x01), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EditableProperty = DependencyProperty.Register(
        nameof(Editable), typeof(bool), typeof(EqGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeDbProperty = DependencyProperty.Register(
        nameof(RangeDb), typeof(double), typeof(EqGraph),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Gains
    {
        get => (double[])GetValue(GainsProperty);
        set => SetValue(GainsProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool Editable
    {
        get => (bool)GetValue(EditableProperty);
        set => SetValue(EditableProperty, value);
    }

    /// <summary>Rango visible (±dB).</summary>
    public double RangeDb
    {
        get => (double)GetValue(RangeDbProperty);
        set => SetValue(RangeDbProperty, value);
    }

    /// <summary>Al soltar un punto tras arrastrarlo.</summary>
    public event Action<double[]>? GainsCommitted;

    static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xEA, 0xEF)));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA2, 0xAB, 0xBA)));
    static readonly Brush ValueBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x28, 0x2F, 0x36)));
    static readonly Typeface LabelFace = new("Segoe UI");
    static readonly Typeface ValueFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    int _drag = -1, _hover = -1;

    double PlotHeight => ActualHeight - LabelHeight;
    double Mid => PlotHeight / 2;
    double Scale => (PlotHeight / 2 - (Editable ? 18 : 6)) / RangeDb;
    double Step(int n) => (ActualWidth - 2 * PadX) / (n - 1);
    double XOf(int i, int n) => PadX + i * Step(n);
    double YOf(double g) => Mid - Util.Clamp(g, -RangeDb, RangeDb) * Scale;

    double[] SafeGains => Gains is { Length: > 1 } g ? g : new double[10];

    int HitTest(Point p)
    {
        var g = SafeGains;
        for (int i = 0; i < g.Length; i++)
        {
            double dx = p.X - XOf(i, g.Length);
            if (Math.Abs(dx) <= Math.Max(10, Step(g.Length) / 2 - 1)) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Editable) return;
        var p = e.GetPosition(this);
        if (_drag >= 0)
        {
            double gain = Math.Round((Mid - p.Y) / Scale * 2) / 2;
            gain = Util.Clamp(gain, Protocol.EqMinDb, Protocol.EqMaxDb);
            var g = (double[])SafeGains.Clone();
            if (g[_drag] != gain)
            {
                g[_drag] = gain;
                Gains = g;
            }
            return;
        }
        int h = HitTest(p);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
        Cursor = h >= 0 ? Cursors.SizeNS : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Editable || !IsEnabled) return;
        _drag = HitTest(e.GetPosition(this));
        if (_drag >= 0)
        {
            CaptureMouse();
            OnMouseMove(e);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        GainsCommitted?.Invoke((double[])SafeGains.Clone());
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag < 0 && _hover >= 0) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = PlotHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        if (w <= 2 * PadX || h <= 0) return;

        var gains = SafeGains;
        int n = gains.Length;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // guias
        var gridPen = new Pen(GridBrush, 1);
        var dashPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
        double guide = Editable ? Protocol.EqMaxDb : 6;
        dc.DrawLine(gridPen, new Point(0, YOf(guide)), new Point(w, YOf(guide)));
        dc.DrawLine(dashPen, new Point(0, Mid), new Point(w, Mid));
        dc.DrawLine(gridPen, new Point(0, YOf(-guide)), new Point(w, YOf(-guide)));
        for (int i = 0; i < n; i++)
        {
            double x = XOf(i, n);
            dc.DrawLine(gridPen, new Point(x, 4), new Point(x, h - 4));
        }

        var pts = new Point[n];
        for (int i = 0; i < n; i++) pts[i] = new Point(XOf(i, n), YOf(gains[i]));

        // curva suave (Catmull-Rom -> Bezier)
        var curve = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var c = curve.Open())
        using (var f = fill.Open())
        {
            c.BeginFigure(pts[0], false, false);
            f.BeginFigure(new Point(pts[0].X, h), true, true);
            f.LineTo(pts[0], false, false);
            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, n - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                c.BezierTo(c1, c2, p2, true, true);
                f.BezierTo(c1, c2, p2, false, true);
            }
            f.LineTo(new Point(pts[n - 1].X, h), false, false);
        }
        curve.Freeze();
        fill.Freeze();

        var grad = new LinearGradientBrush(
            Color.FromArgb(70, Accent.R, Accent.G, Accent.B),
            Color.FromArgb(0, Accent.R, Accent.G, Accent.B), 90);
        dc.DrawGeometry(grad, null, fill);
        var accent = new SolidColorBrush(Accent);
        dc.DrawGeometry(null, new Pen(accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, curve);

        var dotPen = new Pen(accent, 2);
        for (int i = 0; i < n; i++)
        {
            bool active = Editable && (i == _drag || (i == _hover && _drag < 0));
            double r = Editable ? (active ? 8 : 6) : 3.5;
            dc.DrawEllipse(active ? accent : Brushes.White, dotPen, pts[i], r, r);
            if (Editable)
            {
                string v = gains[i] > 0 ? $"+{gains[i]:0.#}" : $"{gains[i]:0.#}";
                var ft = new FormattedText(v, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ValueFace, 10.5, ValueBrush, dpi);
                double ty = gains[i] >= 0 ? pts[i].Y - r - ft.Height - 2 : pts[i].Y + r + 2;
                dc.DrawText(ft, new Point(pts[i].X - ft.Width / 2, ty));
            }
        }

        // etiquetas de frecuencia
        for (int i = 0; i < n; i++)
        {
            int hz = i < Protocol.EqBands.Length ? Protocol.EqBands[i] : 0;
            string label = hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(CultureInfo.InvariantCulture);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, 10, LabelBrush, dpi);
            dc.DrawText(ft, new Point(pts[i].X - ft.Width / 2, h + 3));
        }
    }
}
