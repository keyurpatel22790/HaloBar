using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>Tells which monitor, if any, something full-screen (a game, a video, a presentation) owns.</summary>
internal static class FullscreenWatcher
{
    /// <summary>The monitor the full-screen app is on, or <see cref="IntPtr.Zero"/> when there is none.</summary>
    public static IntPtr FullscreenMonitor(ICollection<IntPtr> ownWindows)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || ownWindows.Contains(fg) || fg == GetShellWindow() || fg == GetDesktopWindow())
            return IntPtr.Zero;
        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);

        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return monitor;

        // Borderless "fullscreen" (browsers, video players) doesn't always set the state above.
        var name = new char[64];
        var len = GetClassNameW(fg, name, name.Length);
        var cls = new string(name, 0, len);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return IntPtr.Zero;

        if (!GetWindowRect(fg, out var r)) return IntPtr.Zero;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return IntPtr.Zero;
        var m = info.rcMonitor;
        // Exactly the monitor: maximised windows overhang it by their border, so they don't match.
        return r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom ? monitor : IntPtr.Zero;
    }
}
