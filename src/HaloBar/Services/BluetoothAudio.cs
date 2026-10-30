using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>
/// Connects and disconnects Bluetooth headphones and speakers the way the Sound settings'
/// Connect button does: each audio endpoint of the device leads, through the device topology, to
/// the Bluetooth audio driver's kernel-streaming filter, which takes a "reconnect" or "disconnect"
/// property request. Windows has no public API to connect other kinds of Bluetooth device.
/// </summary>
internal static class BluetoothAudio
{
    private static readonly Guid BtAudioPropertySet = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint OneShotReconnect = 0, OneShotDisconnect = 1, KsPropertyTypeGet = 1;
    private static readonly PropertyKey DeviceContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>
    /// Sends the request to every Bluetooth audio endpoint of the device with this container id
    /// (or, failing that, whose name contains <paramref name="name"/>). False when it has none.
    /// </summary>
    public static bool Set(Guid? container, string name, bool connect)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var sent = false;
        try
        {
            // Every state: a disconnected headset's endpoints are "unplugged" or "not present".
            if (enumerator.EnumAudioEndpoints(2 /* both directions */, 0xF, out var endpoints) != 0) return false;
            endpoints.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (endpoints.Item(i, out var device) != 0) continue;
                try
                {
                    if (!Belongs(device, container, name)) continue;
                    if (Filter(enumerator, device) is not { } control) continue;
                    var property = new KsProperty { Set = BtAudioPropertySet, Id = connect ? OneShotReconnect : OneShotDisconnect, Flags = KsPropertyTypeGet };
                    var hr = control.KsProperty(ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                    Marshal.ReleaseComObject(control);
                    if (hr >= 0) sent = true;
                    else Log.Write($"Bluetooth: {(connect ? "connect" : "disconnect")} request for {name} failed 0x{hr:X8}");
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // Not a Bluetooth endpoint, or one Windows won't open: skip it.
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(endpoints);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return sent;
    }

    private static bool Belongs(IMMDevice device, Guid? container, string name)
    {
        if (device.OpenPropertyStore(0 /* read */, out var store) != 0) return false;
        try
        {
            if (container is { } wanted && Read(store, DeviceContainerId) is Guid g) return g == wanted;
            // "Headphones (OnePlus Buds 3)"
            return Read(store, FriendlyName) is string friendly && friendly.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The kernel-streaming control of the Bluetooth driver's filter behind an endpoint.</summary>
    private static IKsControl? Filter(IMMDeviceEnumerator enumerator, IMMDevice device)
    {
        var topologyId = typeof(IDeviceTopology).GUID;
        if (device.Activate(ref topologyId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var o) != 0) return null;
        var topology = (IDeviceTopology)o;
        try
        {
            if (topology.GetConnector(0, out var connector) != 0) return null;
            try
            {
                // Only Bluetooth drivers (BTHENUM for A2DP, BTHHFENUM for hands-free) know the property.
                if (connector.GetDeviceIdConnectedTo(out var filterId) != 0 || !filterId.Contains("BTH", StringComparison.OrdinalIgnoreCase)) return null;
                // The filter is a device of its own; it is there even while the headset is away.
                if (enumerator.GetDevice(filterId, out var filter) != 0) return null;
                try
                {
                    var controlId = typeof(IKsControl).GUID;
                    return filter.Activate(ref controlId, 1, IntPtr.Zero, out var control) == 0 ? (IKsControl)control : null;
                }
                finally
                {
                    Marshal.ReleaseComObject(filter);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(connector);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(topology);
        }
    }

    private static object? Read(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        if (store.GetValue(ref key, out value) != 0) return null;
        try
        {
            return value.Type switch
            {
                72 /* VT_CLSID */ => Marshal.PtrToStructure<Guid>(value.Pointer),
                31 /* VT_LPWSTR */ => Marshal.PtrToStringUni(value.Pointer),
                _ => null,
            };
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant v);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IntPtr other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
    }
}
