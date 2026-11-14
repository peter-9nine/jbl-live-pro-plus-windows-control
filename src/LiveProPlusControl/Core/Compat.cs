// Utilidades para .NET Framework 4.8 (APIs que solo existen en .NET moderno).

namespace System.Runtime.CompilerServices
{
    /// <summary>Necesario para "record" / "init" en .NET Framework.</summary>
    internal static class IsExternalInit { }
}

namespace LiveProPlusControl
{
    internal static class Util
    {
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
        public static string Hex(byte[] d) => BitConverter.ToString(d).Replace("-", "");
    }
}
