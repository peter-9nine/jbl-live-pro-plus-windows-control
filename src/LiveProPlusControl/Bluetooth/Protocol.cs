using System.Text;

namespace LiveProPlusControl;

/// <summary>
/// Protocolo de control de JBL LIVE PRO+ TWS (chip BES, "VERSION_OLD"),
/// obtenido de la app JBL Headphones 5.38.2 y verificado con los auriculares.
/// Paquete: AA &lt;cmd&gt; &lt;len&gt; &lt;payload...&gt; (sin checksum).
/// </summary>
public static class Protocol
{
    public const byte Header = 0xAA;

    public const byte DevAck = 0x00;          // AA 00 02 <cmd> <status>
    public const byte AppAck = 0x01;          // AA 01 02 <cmd> 00
    public const byte DevFinAck = 0x04;       // AA 04 <cmd>
    public const byte AppFinAck = 0x05;       // AA 05 <cmd>
    public const byte ReqDevInfo = 0x11;
    public const byte RetDevInfo = 0x12;
    public const byte ReqDevStatus = 0x21;
    public const byte RetDevStatus = 0x22;
    public const byte ReqBeeping = 0x23;
    public const byte RetBeeping = 0x24;
    public const byte Battery = 0x25;
    public const byte SetAnc = 0x31;
    public const byte SetAmbient = 0x32;
    public const byte SetAutoOff = 0x33;
    public const byte SetAutoPlayPause = 0x35;
    public const byte SetBeeping = 0x36;
    public const byte SetEqPreset = 0x40;
    public const byte SetCustomEq = 0x41;
    public const byte ReqCustomEq = 0x42;
    public const byte RetCustomEq = 0x43;
    public const byte GestureBatch = 0x77;
    public const byte SetSmartSwitch = 0x81;
    public const byte ReqSmartSwitch = 0x82;
    public const byte RetSmartSwitch = 0x83;

    // Subtipos de estado (CmdBase.DEV_STATUSl_STATUS_TYPE)
    public const byte StatusAll = 0x30;
    public const byte StatusSealing = 0x42;
    public const byte StatusInEar = 0x41;

    // EnumAAStatus
    public const byte AmbientOff = 0, AmbientTalkThru = 1, AmbientAware = 2;

    // ------------------------------------------------------------------ ecualizador

    /// <summary>Bandas del ecualizador grafico (Hz).</summary>
    public static readonly int[] EqBands = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>Rango de ganancia del modelo (product_list.json: eqGainRange).</summary>
    public const double EqMinDb = -6, EqMaxDb = 6;

    /// <summary>Valor del preset "personalizado" en el byte de estado (EqCatIndex.CUSTOM_EQ).</summary>
    public const byte EqCustomValue = 0x04;

    /// <summary>Presets de este modelo (assets/eq/jbl_live_pro+_tws.json).</summary>
    public static readonly IReadOnlyList<EqPreset> EqPresets =
    [
        new("Off",   0x00, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("Jazz",  0x01, [4, 2, 1, 2.5, -1.5, -1.5, 0, 1, 2, 4]),
        new("Vocal", 0x02, [-1.5, -3, -3, 1, 3.5, 3.5, 3, 1.5, 0, -1.5]),
        new("Bass",  0x03, [6, 3, 3, 2.5, 0.5, 0, 0, 0, 0, 0]),
    ];

    /// <summary>Indice en la UI del ecualizador personalizado (despues de los presets).</summary>
    public static int EqCustomIndex => EqPresets.Count;

    public static int EqIndexOf(byte value)
    {
        if (value == EqCustomValue) return EqCustomIndex;
        for (int i = 0; i < EqPresets.Count; i++)
            if (EqPresets[i].Value == value) return i;
        return -1;
    }

    /// <summary>
    /// Ecualizador personalizado (CmdGen.generateSetCustomEqCmd): indice USER(4), categoria GRAPHIC(1),
    /// calibracion, 48 kHz, ganancias L/R, n.º de bandas y por banda: tipo(1) ganancia fc Q(1.0).
    /// Se envia troceado (EqCmdData): cabecera de 4 bytes, trozos de 79 bytes con indice y cierre AA 05 41.
    /// </summary>
    public static List<byte[]> CustomEqPackets(double[] gains)
    {
        var p = new List<byte> { 4, 1 };
        p.AddRange(BitConverter.GetBytes(0f));   // calibracion (la app usa 0 si no tiene su libreria nativa)
        p.Add(48);                               // frecuencia de muestreo (kHz)
        p.AddRange(BitConverter.GetBytes(0f));   // ganancia izquierda
        p.AddRange(BitConverter.GetBytes(0f));   // ganancia derecha
        p.AddRange(BitConverter.GetBytes(EqBands.Length));
        for (int i = 0; i < EqBands.Length; i++)
        {
            float g = (float)Util.Clamp(i < gains.Length ? gains[i] : 0, EqMinDb, EqMaxDb);
            p.AddRange(BitConverter.GetBytes(1));          // tipo de filtro (peaking)
            p.AddRange(BitConverter.GetBytes(g));
            p.AddRange(BitConverter.GetBytes((float)EqBands[i]));
            p.AddRange(BitConverter.GetBytes(1f));         // Q
        }
        var payload = p.ToArray();
        int chunks = (payload.Length + 78) / 79;
        var list = new List<byte[]> { new[] { Header, SetCustomEq, (byte)payload.Length, (byte)chunks } };
        for (int i = 0; i < chunks; i++)
        {
            int n = Math.Min(79, payload.Length - i * 79);
            var c = new byte[n + 1];
            c[0] = (byte)i;
            Array.Copy(payload, i * 79, c, 1, n);
            list.Add(c);
        }
        list.Add([Header, AppFinAck, SetCustomEq]);
        return list;
    }

    /// <summary>Datos recibidos de AA 42 (sin calibracion): idx cat sr gL gR count bandas[tipo gain fc q].</summary>
    public static double[]? ParseCustomEq(byte[] data)
    {
        if (data.Length < 15 || data[1] != 1) return null;   // categoria GRAPHIC
        int count = BitConverter.ToInt32(data, 11);
        if (count != EqBands.Length || data.Length < 15 + count * 16) return null;
        var g = new double[count];
        for (int i = 0; i < count; i++)
            g[i] = Math.Round(BitConverter.ToSingle(data, 15 + i * 16 + 4) * 2) / 2;
        return g;
    }

    // ------------------------------------------------------------------ Smart Audio & Video

    /// <summary>product_list.json → smartAudioVideo (aac, sbc, latencia).</summary>
    public static readonly IReadOnlyDictionary<SmartMode, ushort[]> SmartModes = new Dictionary<SmartMode, ushort[]>
    {
        [SmartMode.Normal] = [197, 41, 279],
        [SmartMode.Audio] = [256, 53, 200],
        [SmartMode.Video] = [197, 41, 150],
    };

    public static byte[] SetSmart(SmartMode m)
    {
        var v = SmartModes[m];
        var p = new byte[8];
        for (int i = 0; i < 3; i++) BitConverter.GetBytes(v[i]).CopyTo(p, i * 2);
        p[6] = 0xFF; p[7] = 0xFF;   // 4.º valor sin usar en este modelo
        return Packet(SetSmartSwitch, p);
    }

    public static SmartMode ParseSmart(byte[] d)
    {
        if (d.Length < 9 || d[1] != RetSmartSwitch) return SmartMode.Unknown;
        ushort a = BitConverter.ToUInt16(d, 3), b = BitConverter.ToUInt16(d, 5), c = BitConverter.ToUInt16(d, 7);
        foreach (var kv in SmartModes)
            if (kv.Value[0] == a && kv.Value[1] == b && kv.Value[2] == c) return kv.Key;
        return SmartMode.Unknown;
    }

    // ------------------------------------------------------------------ gestos

    /// <summary>Gestos editables (GestureType ordinal).</summary>
    public const byte GestureLeftTap = 6, GestureLeftDouble = 7, GestureLeftTriple = 8,
                      GestureRightTap = 9, GestureRightDouble = 10, GestureRightTriple = 11;

    /// <summary>Acciones (assets/gesture/common_gesture_config.json → functionList).</summary>
    public static readonly IReadOnlyList<GestureAction> GestureActions =
    [
        new(0x08, "Play / Pause"),
        new(0x05, "Next track"),
        new(0x06, "Previous track"),
        new(0x01, "Volume up"),
        new(0x02, "Volume down"),
        new(0x0B, "ANC ↔ Ambient Aware"),
        new(0x09, "ANC → Ambient → Off"),
        new(0x04, "TalkThru"),
        new(0xA1, "Voice assistant"),
        new(0x00, "None"),
    ];

    public static byte[] QueryGestures() => Packet(GestureBatch, 0x01, 0xFF);

    public static byte[] SetGesture(byte gesture, byte action) => Packet(GestureBatch, 0x00, gesture, action);

    /// <summary>AA 77 len 02 (gesto accion)* → mapa gesto → accion.</summary>
    public static Dictionary<byte, byte>? ParseGestures(byte[] d)
    {
        if (d.Length < 4 || d[1] != GestureBatch || d[3] != 0x02) return null;
        var map = new Dictionary<byte, byte>();
        for (int i = 4; i + 1 < d.Length; i += 2) map[d[i]] = d[i + 1];
        return map;
    }

    // ------------------------------------------------------------------ Find My Buds

    /// <summary>BesBeepingType → values_ear_beeping: parar der/izq = 00/01, iniciar der/izq = 10/11.</summary>
    public static byte[] Beep(bool left, bool start) =>
        Packet(SetBeeping, (byte)((start ? 0x10 : 0x00) | (left ? 0x01 : 0x00)));

    public static byte[] QueryBeeping() => Packet(ReqBeeping);

    /// <summary>AA 24 01 xx: 01 ambos, 02 derecho, 03 izquierdo, 00 ninguno.</summary>
    public static (bool Left, bool Right)? ParseBeeping(byte[] d)
    {
        if (d.Length < 4 || d[1] != RetBeeping) return null;
        return d[3] switch
        {
            0x01 => (true, true),
            0x02 => (false, true),
            0x03 => (true, false),
            _ => (false, false),
        };
    }

    // ------------------------------------------------------------------ en el oido

    public static byte[] QueryInEar() => Packet(ReqDevStatus, StatusInEar);

    /// <summary>AA 22 03 41 L R — 1 = puesto en el oido.</summary>
    public static (bool Left, bool Right)? ParseInEar(byte[] d)
    {
        if (d.Length < 6 || d[1] != RetDevStatus || d[3] != StatusInEar) return null;
        return (d[4] == 1, d[5] == 1);
    }

    // ------------------------------------------------------------------ Check My Best Fit

    public static byte[] QuerySealing() => Packet(ReqDevStatus, StatusSealing);

    /// <summary>AA 22 03 42 L R — 1 = buen sellado, 0 = ajustar.</summary>
    public static (bool Left, bool Right)? ParseSealing(byte[] d)
    {
        if (d.Length < 6 || d[1] != RetDevStatus || d[3] != StatusSealing) return null;
        return (d[4] == 1, d[5] == 1);
    }

    // ------------------------------------------------------------------ comandos basicos

    public static byte[] Packet(byte cmd, params byte[] payload)
    {
        var p = new byte[3 + payload.Length];
        p[0] = Header;
        p[1] = cmd;
        p[2] = (byte)payload.Length;
        payload.CopyTo(p, 3);
        return p;
    }

    public static byte[] QueryInfo() => Packet(ReqDevInfo);
    public static byte[] QueryStatus() => Packet(ReqDevStatus, StatusAll);
    public static byte[] QueryBattery() => [Header, Battery, 0x01, 0x00]; // CmdBase.combineSub()
    public static byte[] QuerySmart() => Packet(ReqSmartSwitch);

    public static byte[][] SetMode(NoiseMode mode) => mode switch
    {
        NoiseMode.Anc => [Packet(SetAnc, 1)],
        NoiseMode.TalkThru => [Packet(SetAmbient, AmbientTalkThru)],
        NoiseMode.Ambient => [Packet(SetAmbient, AmbientAware)],
        _ => [Packet(SetAnc, 0), Packet(SetAmbient, AmbientOff)],
    };

    public static byte[] SetEq(byte preset) => Packet(SetEqPreset, preset);

    public static byte[] AutoOff(bool on, int minutes) =>
        Packet(SetAutoOff, (byte)(((on ? 1 : 0) << 7) | (Util.Clamp(minutes, 0, 127) & 0x7F)));

    public static byte[] AutoPlayPause(bool on) => Packet(SetAutoPlayPause, (byte)(on ? 1 : 0));

    // ---------------- parseo ----------------

    static int? Pct(byte b)
    {
        int lvl = b & 0x7F;
        return b == 0xFF || lvl > 100 ? null : lvl;
    }

    static bool Flag(byte b) => b != 0 && b != 0xFF;

    /// <summary>aa 22 len 30 | anc aa autooff eq multiAI ? ota autoPlay tws ...</summary>
    public static DeviceStatus? ParseStatus(byte[] d)
    {
        if (d.Length < 8 || d[1] != RetDevStatus || d[3] != StatusAll) return null;
        var s = new DeviceStatus();
        byte anc = d[4], aa = d[5];
        s.Mode = anc is 1 or 2 ? NoiseMode.Anc
               : aa == AmbientAware ? NoiseMode.Ambient
               : aa == AmbientTalkThru ? NoiseMode.TalkThru
               : NoiseMode.Off;
        if (d[6] != 0xFF)
        {
            s.AutoOffOn = (d[6] & 0x80) != 0;
            s.AutoOffMinutes = d[6] & 0x7F;
        }
        s.EqValue = d[7];
        if (d.Length > 11) s.AutoPlayPause = d[11] != 0;
        if (d.Length > 12) s.TwsConnected = d[12] != 0;
        return s;
    }

    public static DeviceInfo ParseInfo(byte[] d)
    {
        var i = new DeviceInfo();
        if (d.Length >= 19)
        {
            int end = Array.IndexOf(d, (byte)0, 3, 16);
            int len = (end < 0 ? 19 : end) - 3;
            i.Name = Encoding.ASCII.GetString(d, 3, len).Trim();
        }
        if (d.Length >= 21) i.Pid = $"{d[20]:X2}{d[19]:X2}";
        if (d.Length >= 22) i.ColorId = d[21];
        if (d.Length >= 29)
            i.Mac = string.Join(":", d.Skip(23).Take(6).Reverse().Select(b => b.ToString("X2")));
        if (d.Length >= 32) i.Firmware = $"{d[29]}.{d[30]}.{d[31]}";
        return i;
    }

    /// <summary>
    /// HandleParse (BATTERY_STATUS, subcomando 01):
    /// aa 25 len 01 | cargaL cargaR | nivelL nivelR | cargaEstuche nivelEstuche | mV L | mV R | mV estuche
    /// El estuche solo reporta cuando al menos un auricular esta dentro (tapa abierta).
    /// </summary>
    public static BatteryInfo? ParseBattery(byte[] d)
    {
        if (d.Length < 10 || d[1] != Battery || d[3] != 0x01) return null;
        return new BatteryInfo
        {
            LeftCharging = Flag(d[4]),
            RightCharging = Flag(d[5]),
            Left = Pct(d[6]),
            Right = Pct(d[7]),
            CaseCharging = Flag(d[8]),
            Case = Pct(d[9]),
        };
    }

    /// <summary>Color del producto (cn.f: id → nombre e imagen live_pro_*).</summary>
    public static (string Name, string Image) ColorOf(int id) => id switch
    {
        1 => ("White", "buds_white.png"),
        2 => ("Blue", "buds_blue.png"),
        9 => ("Beige", "buds_beige.png"),
        10 => ("Pink", "buds_pink.png"),
        11 => ("Chrome", "buds_chrome.png"),
        12 => ("Rose gold", "buds_rose_gold.png"),
        _ => ("Black", "buds_black.png"),
    };
}

public sealed record EqPreset(string Name, byte Value, double[] Gains);

public sealed record GestureAction(byte Value, string Name)
{
    public override string ToString() => Name;
}

public enum NoiseMode { Off, Anc, Ambient, TalkThru }

public enum SmartMode { Unknown, Normal, Audio, Video }

public sealed class DeviceStatus
{
    public NoiseMode Mode { get; set; }
    public bool? AutoOffOn { get; set; }
    public int? AutoOffMinutes { get; set; }
    public byte EqValue { get; set; }
    public bool? AutoPlayPause { get; set; }
    public bool? TwsConnected { get; set; }
}

public sealed class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Firmware { get; set; } = "";
    public int ColorId { get; set; }
}

public sealed class BatteryInfo
{
    public int? Left { get; set; }
    public int? Right { get; set; }
    public int? Case { get; set; }
    public bool LeftCharging { get; set; }
    public bool RightCharging { get; set; }
    public bool CaseCharging { get; set; }
}
