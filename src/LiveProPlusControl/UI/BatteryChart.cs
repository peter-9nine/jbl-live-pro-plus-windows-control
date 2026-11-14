using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiveProPlusControl;

/// <summary>
/// Grafica de nivel de bateria (L, R, estuche) a lo largo de la sesion.
/// Lineas escalonadas (el nivel cambia por saltos), etiquetas directas al final
/// y cruz + tooltip al pasar el raton.
/// </summary>
public sealed class BatteryChart : FrameworkElement
{
    // Series: color de identidad + trazo
    static readonly Brush LeftBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x59, 0x01)));
    static readonly Brush RightBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x59, 0x6B)));
    static readonly Brush CaseBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x98, 0xFA)));

    static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xEA, 0xEF)));
    static readonly Brush AxisText = Frozen(new SolidColorBrush(Color.FromRgb(0xA2, 0xAB, 0xBA)));
    static readonly Brush Ink1 = Frozen(new SolidColorBrush(Color.FromRgb(0x28, 0x2F, 0x36)));
    static readonly Brush Ink2 = Frozen(new SolidColorBrush(Color.FromRgb(0x61, 0x70, 0x87)));
    static readonly Brush Surface = Brushes.White;
    static readonly Typeface Face = new("Segoe UI");
    static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    const double PadLeft = 34, PadRight = 64, PadTop = 10, PadBottom = 22;

    BatteryLog? _log;
    Point? _mouse;

    public BatteryLog? Log
    {
        get => _log;
        set
        {
            if (_log != null) _log.Changed -= InvalidateVisual;
            _log = value;
            if (_log != null) _log.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    public BatteryChart()
    {
        MouseMove += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouse = null; InvalidateVisual(); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        // fondo transparente pero "hit-testable" para el hover
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w <= PadLeft + PadRight || h <= PadTop + PadBottom) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);

        var samples = _log?.Samples ?? [];
        DateTime t0 = _log?.SessionStart ?? DateTime.Now;
        DateTime t1 = DateTime.Now;
        if ((t1 - t0).TotalMinutes < 10) t1 = t0.AddMinutes(10);
        double span = (t1 - t0).TotalSeconds;

        double X(DateTime t) => plot.Left + (t - t0).TotalSeconds / span * plot.Width;
        double Y(double pct) => plot.Bottom - pct / 100 * plot.Height;

        // ---- rejilla y ejes (recesivos)
        var gridPen = new Pen(GridBrush, 1);
        foreach (int p in new[] { 0, 25, 50, 75, 100 })
        {
            double y = Math.Round(Y(p)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text($"{p}%", 10, AxisText, Face, dpi);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }
        foreach (var (t, label) in TimeTicks(t0, t1))
        {
            var ft = Text(label, 10, AxisText, Face, dpi);
            dc.DrawText(ft, new Point(Util.Clamp(X(t) - ft.Width / 2, plot.Left - 10, plot.Right - ft.Width + 10), plot.Bottom + 5));
        }

        if (samples.Count == 0)
        {
            var ft = Text("Waiting for battery data…", 12, Ink2, Face, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
            return;
        }

        // ---- series (estuche primero, para quedar debajo)
        DrawSeries(dc, samples, s => s.Case, CaseBrush, dashed: true, X, Y, t1);
        DrawSeries(dc, samples, s => s.Right, RightBrush, dashed: false, X, Y, t1);
        DrawSeries(dc, samples, s => s.Left, LeftBrush, dashed: false, X, Y, t1);

        // ---- etiquetas directas al final de cada linea (texto en tinta, punto con el color)
        var ends = new List<(double Y, string Text, Brush Color)>();
        void End(Func<BatterySample, int?> f, string name, Brush c)
        {
            var last = samples.LastOrDefault(s => f(s) != null);
            if (last != null && samples[samples.Count - 1] is var tail && f(tail) != null)
                ends.Add((Y(f(last)!.Value), $"{name} {f(last)}%", c));
        }
        End(s => s.Left, "L", LeftBrush);
        End(s => s.Right, "R", RightBrush);
        End(s => s.Case, "Case", CaseBrush);
        ends.Sort((a, b) => a.Y.CompareTo(b.Y));
        double minGap = 14, prevY = double.MinValue;
        foreach (var (y0, text, color) in ends)
        {
            double y = Math.Max(y0, prevY + minGap);
            prevY = y;
            dc.DrawEllipse(color, null, new Point(plot.Right + 8, y), 3.5, 3.5);
            var ft = Text(text, 11, Ink1, FaceBold, dpi);
            dc.DrawText(ft, new Point(plot.Right + 15, y - ft.Height / 2));
        }

        // ---- hover: cruz + tooltip
        if (_mouse is Point m && plot.Contains(new Point(Util.Clamp(m.X, plot.Left, plot.Right), plot.Top + 1)))
        {
            DateTime tm = t0.AddSeconds((Util.Clamp(m.X, plot.Left, plot.Right) - plot.Left) / plot.Width * span);
            var at = samples.LastOrDefault(s => s.Time <= tm);
            if (at == null) return;
            double x = Math.Round(Util.Clamp(m.X, plot.Left, plot.Right)) + 0.5;
            dc.DrawLine(new Pen(Ink2, 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(x, plot.Top), new Point(x, plot.Bottom));

            string F(int? v) => v is int i ? $"{i}%" : "--";
            var lines = new List<(string, Brush?)>
            {
                ($"{tm:HH:mm}", null),
                ($"Left  {F(at.Left)}{(at.LeftCharging ? " ⚡" : "")}", LeftBrush),
                ($"Right  {F(at.Right)}{(at.RightCharging ? " ⚡" : "")}", RightBrush),
            };
            if (at.Case != null) lines.Add(($"Case  {F(at.Case)}", CaseBrush));

            var texts = lines.Select(l => (Ft: Text(l.Item1, 11.5, l.Item2 == null ? Ink2 : Ink1, l.Item2 == null ? Face : FaceBold, dpi), Dot: l.Item2)).ToList();
            double tw = texts.Max(t => t.Ft.Width) + 32, th = texts.Sum(t => t.Ft.Height + 2) + 14;
            double tx = x + 12 + tw > w ? x - 12 - tw : x + 12;
            double ty = Util.Clamp(m.Y - th / 2, 0, h - th);
            dc.DrawRoundedRectangle(Surface, new Pen(GridBrush, 1), new Rect(tx, ty, tw, th), 8, 8);
            double cy = ty + 7;
            foreach (var (ft, dot) in texts)
            {
                if (dot != null) dc.DrawEllipse(dot, null, new Point(tx + 14, cy + ft.Height / 2), 3.5, 3.5);
                dc.DrawText(ft, new Point(tx + (dot != null ? 24 : 10), cy));
                cy += ft.Height + 2;
            }
        }
    }

    /// <summary>Linea escalonada de 2 px; se corta donde no hay datos.</summary>
    static void DrawSeries(DrawingContext dc, IReadOnlyList<BatterySample> samples, Func<BatterySample, int?> f,
                           Brush brush, bool dashed, Func<DateTime, double> X, Func<double, double> Y, DateTime now)
    {
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) pen.DashStyle = new DashStyle([2.5, 2], 0);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            bool open = false;
            double lastY = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var v = f(samples[i]);
                if (v is null)
                {
                    open = false;
                    continue;
                }
                double x = X(samples[i].Time), y = Y(v.Value);
                if (!open) { c.BeginFigure(new Point(x, y), false, false); open = true; }
                else { c.LineTo(new Point(x, lastY), true, true); c.LineTo(new Point(x, y), true, true); }
                lastY = y;
                // extiende hasta "ahora" si es la ultima muestra valida
                bool lastValid = i == samples.Count - 1;
                if (lastValid) c.LineTo(new Point(X(now), y), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    static IEnumerable<(DateTime, string)> TimeTicks(DateTime t0, DateTime t1)
    {
        double minutes = (t1 - t0).TotalMinutes;
        int step = minutes <= 15 ? 5 : minutes <= 60 ? 15 : minutes <= 180 ? 30 : minutes <= 360 ? 60 : 120;
        var first = new DateTime(t0.Year, t0.Month, t0.Day, t0.Hour, 0, 0);
        for (var t = first; t <= t1; t = t.AddMinutes(step))
            if (t >= t0) yield return (t, t.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    static FormattedText Text(string s, double size, Brush b, Typeface face, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, b, dpi);
}
