using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>Switches Wi-Fi's software radio through the WLAN API, for when the Radios API fails.</summary>
internal static class WlanRadio
{
    private const int OpcodeRadioState = 4; // wlan_intf_opcode_radio_state

    public static bool Set(bool on)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return false;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return false;
            var done = false;
            try
            {
                // WLAN_INTERFACE_INFO_LIST: two DWORDs, then entries of a GUID, a 256-char name and a state.
                var count = Marshal.ReadInt32(list);
                const int entrySize = 16 + 512 + 4;
                for (var i = 0; i < count; i++)
                {
                    var id = Marshal.PtrToStructure<Guid>(list + 8 + i * entrySize);
                    var state = new PhyRadioState { PhyIndex = 0, Software = on ? 1 : 2, Hardware = on ? 1 : 2 };
                    var size = Marshal.SizeOf<PhyRadioState>();
                    var data = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(state, data, false);
                        var hr = WlanSetInterface(handle, ref id, OpcodeRadioState, (uint)size, data, IntPtr.Zero);
                        if (hr == 0) done = true;
                        else Log.Write($"Controls: WLAN radio switch failed ({hr})");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(data);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
            return done;
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PhyRadioState
    {
        public uint PhyIndex;
        public int Software;
        public int Hardware;
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanSetInterface(IntPtr handle, ref Guid id, int opcode, uint size, IntPtr data, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
