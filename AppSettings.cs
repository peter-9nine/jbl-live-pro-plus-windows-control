using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace JblControl;

/// <summary>Configuracion persistente en %AppData%\LiveProPlusControl\settings.ini (clave=valor).</summary>
public sealed class AppSettings
{
    public ulong LastAddress { get; set; }
    public bool TrayHintShown { get; set; }

    /// <summary>Al volver el audio de los auriculares, limitar el volumen de Windows.</summary>
    public bool SafeVolumeEnabled { get; set; } = true;

    /// <summary>Volumen maximo (%) aplicado al volver el audio.</summary>
    public int SafeVolumeLevel { get; set; } = 30;

    /// <summary>Boton cerrar: 0 = preguntar, 1 = minimizar a la bandeja, 2 = salir.</summary>
    public int CloseAction { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveProPlusControl", "settings.ini");

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case nameof(LastAddress):
                        ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a);
                        s.LastAddress = a;
                        break;
                    case nameof(TrayHintShown):
                        s.TrayHintShown = val == "1";
                        break;
                    case nameof(CloseAction):
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ca))
                            s.CloseAction = Util.Clamp(ca, 0, 2);
                        break;
                    case nameof(SafeVolumeEnabled):
                        s.SafeVolumeEnabled = val != "0";
                        break;
                    case nameof(SafeVolumeLevel):
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
                            s.SafeVolumeLevel = Util.Clamp(lv, 5, 80);
                        break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath,
            [
                $"{nameof(LastAddress)}={LastAddress.ToString(CultureInfo.InvariantCulture)}",
                $"{nameof(TrayHintShown)}={(TrayHintShown ? 1 : 0)}",
                $"{nameof(SafeVolumeEnabled)}={(SafeVolumeEnabled ? 1 : 0)}",
                $"{nameof(CloseAction)}={CloseAction.ToString(CultureInfo.InvariantCulture)}",
                $"{nameof(SafeVolumeLevel)}={SafeVolumeLevel.ToString(CultureInfo.InvariantCulture)}",
            ]);
        }
        catch { }
    }

    // ---- Inicio con Windows (HKCU\...\Run) ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "LiveProPlusControl";

    static string ExePath => System.Reflection.Assembly.GetEntryAssembly()!.Location;

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                k.SetValue(RunName, $"\"{ExePath}\" --tray");
            else
                k.DeleteValue(RunName, false);
        }
    }
}
