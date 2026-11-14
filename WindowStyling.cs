using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JblControl;

/// <summary>Esquinas redondeadas nativas de Windows 11 (sin relleno negro detras de las esquinas).</summary>
public static class WindowStyling
{
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void RoundCorners(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* Windows 10: sin efecto */ }
    }
}
