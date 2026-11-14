using System.Runtime.InteropServices;

namespace JblControl;

/// <summary>
/// Acceso a las salidas de audio de Windows (Core Audio) de los auriculares:
///  - estado de la salida estereo (A2DP),
///  - volumen (para bajarlo a un nivel seguro cuando el audio vuelve),
///  - reconexion del audio Bluetooth sin permisos de administrador
///    (KSPROPSETID_BtAudio / KSPROPERTY_ONESHOT_RECONNECT, igual que hace ToothTray).
/// </summary>
public static class AudioEndpoints
{
    public const string NameMatch = "LIVE PRO+";

    public sealed class Endpoint
    {
        public string Id = "";
        public string Name = "";
        public uint State;          // 1 = activo, 4 = desconectado (unplugged), 2 = deshabilitado
        public bool IsHandsFree;
        public bool IsActive => State == DEVICE_STATE_ACTIVE;
        public override string ToString() => $"{Name} [{(IsActive ? "activo" : "estado " + State)}]";
    }

    const uint DEVICE_STATE_ACTIVE = 0x1;
    const uint DEVICE_STATEMASK_ALL = 0xF;
    const int eRender = 0;
    const int CLSCTX_ALL = 0x17;
    const int CLSCTX_INPROC_SERVER = 0x1;

    static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly Guid IID_IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    static readonly Guid IID_IKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
    const uint KSPROPERTY_TYPE_GET = 1;

    /// <summary>Salidas de audio (render) de los auriculares, en cualquier estado.</summary>
    public static List<Endpoint> Find()
    {
        var list = new List<Endpoint>();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(en.EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL, out var col));
            Check(col.GetCount(out uint n));
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                try
                {
                    string name = FriendlyName(dev);
                    if (name.IndexOf(NameMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    dev.GetId(out string id);
                    dev.GetState(out uint st);
                    list.Add(new Endpoint
                    {
                        Id = id,
                        Name = name,
                        State = st,
                        IsHandsFree = name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0,
                    });
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            Marshal.ReleaseComObject(col);
        }
        finally { Marshal.ReleaseComObject(en); }
        return list;
    }

    /// <summary>La salida estereo (A2DP) de los auriculares, si existe.</summary>
    public static Endpoint? Stereo() => Find().FirstOrDefault(e => !e.IsHandsFree);

    static string FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0) return "";
        try
        {
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return "";
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "" : ""; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static T WithDevice<T>(string id, Func<IMMDevice, T> f)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(en.GetDevice(id, out var dev));
            try { return f(dev); }
            finally { Marshal.ReleaseComObject(dev); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Volumen maestro (0..1) de una salida activa.</summary>
    public static float? GetVolume(string id)
    {
        try
        {
            return WithDevice<float?>(id, dev =>
            {
                var iid = IID_IAudioEndpointVolume;
                Check(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o));
                var vol = (IAudioEndpointVolume)o;
                try { Check(vol.GetMasterVolumeLevelScalar(out float v)); return v; }
                finally { Marshal.ReleaseComObject(vol); }
            });
        }
        catch { return null; }
    }

    public static bool SetVolume(string id, float level)
    {
        try
        {
            return WithDevice(id, dev =>
            {
                var iid = IID_IAudioEndpointVolume;
                Check(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o));
                var vol = (IAudioEndpointVolume)o;
                try
                {
                    var ctx = Guid.Empty;
                    Check(vol.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref ctx));
                    return true;
                }
                finally { Marshal.ReleaseComObject(vol); }
            });
        }
        catch (Exception ex) { Log.Write("SetVolume: " + ex.Message); return false; }
    }

    /// <summary>
    /// Pide a Windows que reconecte el audio Bluetooth de esa salida.
    /// Equivale a "Conectar" en la configuracion de Bluetooth; no requiere administrador.
    /// </summary>
    public static bool Reconnect(string id) => SendBtAudio(id, KSPROPERTY_ONESHOT_RECONNECT);

    /// <summary>Desconecta el audio Bluetooth de esa salida (solo para pruebas).</summary>
    public static bool Disconnect(string id) => SendBtAudio(id, KSPROPERTY_ONESHOT_DISCONNECT);

    static bool SendBtAudio(string id, uint propId)
    {
        try
        {
            // endpoint -> conector 0 -> parte conectada (filtro KS del audio Bluetooth)
            string? filterId = WithDevice(id, dev =>
            {
                var iid = IID_IDeviceTopology;
                Check(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o));
                var topo = (IDeviceTopology)o;
                IConnector? conn = null, to = null;
                try
                {
                    Check(topo.GetConnector(0, out conn));
                    Check(conn.GetConnectedTo(out to));
                    var part = (IPart)to;
                    Check(part.GetTopologyObject(out IntPtr ft));
                    var filterTopo = (IDeviceTopology)Marshal.GetObjectForIUnknown(ft);
                    Marshal.Release(ft);
                    try
                    {
                        Check(filterTopo.GetDeviceId(out string fid));
                        return fid;
                    }
                    finally { Marshal.ReleaseComObject(filterTopo); }
                }
                finally
                {
                    if (to != null) Marshal.ReleaseComObject(to);
                    if (conn != null) Marshal.ReleaseComObject(conn);
                    Marshal.ReleaseComObject(topo);
                }
            });
            if (filterId is null) return false;

            // el filtro como IMMDevice -> IKsControl -> KSPROPERTY_ONESHOT_RECONNECT
            return WithDevice(filterId, dev =>
            {
                var ksIid = IID_IKsControl;
                Check(dev.Activate(ref ksIid, CLSCTX_ALL, IntPtr.Zero, out object ko));
                var ks = (IKsControl)ko;
                try
                {
                    var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = propId, Flags = KSPROPERTY_TYPE_GET };
                    int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                    Log.Write($"Audio Bluetooth {(propId == KSPROPERTY_ONESHOT_RECONNECT ? "reconectar" : "desconectar")} (hr=0x{hr:X8})");
                    return hr == 0;
                }
                finally { Marshal.ReleaseComObject(ks); }
            });
        }
        catch (Exception ex) { Log.Write("Reconnect audio: " + ex.Message); return false; }
    }

    static void Check(int hr)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ------------------------------------------------------------------ COM

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        ushort r1, r2, r3;
        public IntPtr p;
        IntPtr p2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector to);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector to);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid subType);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr iface);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IntPtr topology);
        [PreserveSig] int Activate(uint clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }
}
