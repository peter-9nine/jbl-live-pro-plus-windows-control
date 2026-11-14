using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveProPlusControl;

public sealed record BatterySample(DateTime Time, int? Left, int? Right, int? Case,
                                   bool LeftCharging, bool RightCharging, bool CaseCharging);

/// <summary>Estadisticas de un auricular durante la sesion.</summary>
public sealed class BudStats
{
    public int? Start { get; set; }
    public int? Current { get; set; }
    /// <summary>Porcentaje consumido (solo bajadas mientras no carga).</summary>
    public int Consumed { get; set; }
    /// <summary>Tiempo con datos y sin cargar.</summary>
    public TimeSpan ActiveTime { get; set; }
    /// <summary>% por hora; null si aun no hay datos suficientes.</summary>
    public double? RatePerHour => ActiveTime.TotalMinutes >= 5 && Consumed > 0 ? Consumed / ActiveTime.TotalHours : null;
    public TimeSpan? Remaining => RatePerHour is double r && Current is int c ? TimeSpan.FromHours(c / r) : null;
}

/// <summary>
/// Historial de bateria y modo de ruido de la sesion actual (solo en memoria;
/// se pierde al cerrar la app).
/// </summary>
public sealed class BatteryLog : INotifyPropertyChanged
{
    readonly List<BatterySample> _samples = [];
    readonly Dictionary<NoiseMode, TimeSpan> _modeTime = [];
    NoiseMode _mode;
    DateTime? _connectedSince;
    TimeSpan _connectedTotal;
    DateTime _modeSince = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Se dispara con cada muestra nueva (para redibujar la grafica).</summary>
    public event Action? Changed;

    public DateTime SessionStart { get; private set; } = DateTime.Now;
    public IReadOnlyList<BatterySample> Samples => _samples;

    public void Add(BatteryInfo b)
    {
        var now = DateTime.Now;
        var last = _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
        var s = new BatterySample(now, b.Left, b.Right, b.Case, b.LeftCharging, b.RightCharging, b.CaseCharging);
        // Evita duplicados seguidos (avisos + sondeo), salvo para marcar el paso del tiempo cada minuto.
        if (last != null && (now - last.Time).TotalSeconds < 60 &&
            last.Left == s.Left && last.Right == s.Right && last.Case == s.Case &&
            last.LeftCharging == s.LeftCharging && last.RightCharging == s.RightCharging)
            return;
        _samples.Add(s);
        RaiseAll();
    }

    public void SetConnected(bool connected)
    {
        var now = DateTime.Now;
        if (connected && _connectedSince is null)
        {
            _connectedSince = now;
            _modeSince = now;
        }
        else if (!connected && _connectedSince is DateTime since)
        {
            AccumulateMode(now);
            _connectedTotal += now - since;
            _connectedSince = null;
            // corte en la grafica
            _samples.Add(new BatterySample(now, null, null, null, false, false, false));
        }
        RaiseAll();
    }

    public void SetMode(NoiseMode mode)
    {
        if (mode == _mode) return;
        AccumulateMode(DateTime.Now);
        _mode = mode;
        RaiseAll();
    }

    void AccumulateMode(DateTime now)
    {
        if (_connectedSince is null) { _modeSince = now; return; }
        _modeTime.TryGetValue(_mode, out var t);
        _modeTime[_mode] = t + (now - _modeSince);
        _modeSince = now;
    }

    public void Reset()
    {
        var now = DateTime.Now;
        var last = _samples.LastOrDefault(x => x.Left != null || x.Right != null);
        _samples.Clear();
        _modeTime.Clear();
        _connectedTotal = TimeSpan.Zero;
        SessionStart = now;
        if (_connectedSince != null) _connectedSince = now;
        _modeSince = now;
        if (last != null && _connectedSince != null) _samples.Add(last with { Time = now });
        RaiseAll();
    }

    // ------------------------------------------------------------------ calculos

    public TimeSpan ConnectedTime =>
        _connectedTotal + (_connectedSince is DateTime s ? DateTime.Now - s : TimeSpan.Zero);

    public TimeSpan TimeInMode(NoiseMode m)
    {
        _modeTime.TryGetValue(m, out var t);
        if (_connectedSince != null && m == _mode) t += DateTime.Now - _modeSince;
        return t;
    }

    public BudStats Stats(Func<BatterySample, int?> level, Func<BatterySample, bool> charging)
    {
        var st = new BudStats();
        BatterySample? prev = null;
        foreach (var s in _samples)
        {
            var lv = level(s);
            if (lv is null) { prev = null; continue; }
            st.Start ??= lv;
            st.Current = lv;
            if (prev != null && level(prev) is int p && !charging(s) && !charging(prev))
            {
                st.ActiveTime += s.Time - prev.Time;
                if (lv < p) st.Consumed += p - lv.Value;
            }
            prev = s;
        }
        // tiempo transcurrido desde la ultima muestra
        if (prev != null && !charging(prev)) st.ActiveTime += DateTime.Now - prev.Time;
        return st;
    }

    public BudStats LeftStats => Stats(s => s.Left, s => s.LeftCharging);
    public BudStats RightStats => Stats(s => s.Right, s => s.RightCharging);

    // ------------------------------------------------------------------ textos para la UI

    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{(int)t.TotalMinutes} min";

    public string ConnectedText => Dur(ConnectedTime);
    public string SessionStartText => $"Since {SessionStart:HH:mm}";

    public string ConsumedText
    {
        get
        {
            var l = LeftStats; var r = RightStats;
            if (l.Start is null && r.Start is null) return "--";
            return $"L −{l.Consumed}%   R −{r.Consumed}%";
        }
    }

    public string RateText
    {
        get
        {
            var rates = new[] { LeftStats.RatePerHour, RightStats.RatePerHour }.Where(x => x != null).Select(x => x!.Value).ToList();
            return rates.Count == 0 ? "Calculating…" : $"≈ {rates.Average():0} %/h";
        }
    }

    public string RemainingText
    {
        get
        {
            var rem = new[] { LeftStats.Remaining, RightStats.Remaining }.Where(x => x != null).Select(x => x!.Value).ToList();
            return rem.Count == 0 ? "Calculating…" : "≈ " + Dur(rem.Min());
        }
    }

    public string ModeAncText => Dur(TimeInMode(NoiseMode.Anc));
    public string ModeAmbientText => Dur(TimeInMode(NoiseMode.Ambient));
    public string ModeTalkText => Dur(TimeInMode(NoiseMode.TalkThru));
    public string ModeOffText => Dur(TimeInMode(NoiseMode.Off));

    double Share(NoiseMode m)
    {
        double total = ConnectedTime.TotalSeconds;
        return total <= 0 ? 0 : TimeInMode(m).TotalSeconds / total;
    }

    public double ModeAncShare => Share(NoiseMode.Anc);
    public double ModeAmbientShare => Share(NoiseMode.Ambient);
    public double ModeTalkShare => Share(NoiseMode.TalkThru);
    public double ModeOffShare => Share(NoiseMode.Off);

    /// <summary>Refresca los textos que dependen de la hora actual.</summary>
    public void Tick() => RaiseAll();

    void RaiseAll([CallerMemberName] string? _ = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke();
    }
}
