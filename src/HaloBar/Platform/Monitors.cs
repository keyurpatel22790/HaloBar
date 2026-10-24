using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

internal sealed record MonitorInfo(IntPtr Handle, RECT Bounds, float Scale, bool IsPrimary)
{
    /// <summary>Identifies the same screen across re-enumeration even if Windows reuses handles.</summary>
    public string Key => $"{Bounds.Left},{Bounds.Top},{Bounds.Width}x{Bounds.Height}@{Scale:0.##}";
}

internal static class Monitors
{
    /// <summary>All connected monitors, the primary one first.</summary>
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96f : 1f;
                list.Add(new MonitorInfo(monitor, info.rcMonitor, scale, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return [.. list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left)];
    }
}
