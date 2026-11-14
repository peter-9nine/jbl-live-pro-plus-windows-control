using System.IO;

namespace LiveProPlusControl;

/// <summary>Registro de diagnostico en %LocalAppData%\LiveProPlusControl\log.txt (se reinicia en cada arranque).</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveProPlusControl", "log.txt");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "");
        }
        catch { }
    }

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static string Hex(byte[] d) => Util.Hex(d);
}
