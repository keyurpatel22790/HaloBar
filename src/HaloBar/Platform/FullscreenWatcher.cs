using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>What the window in front is doing, as far as the island cares.</summary>
internal readonly record struct ForegroundInfo(
    IntPtr Monitor,
    string Process,
    bool Fullscreen,
    bool Game,
    bool Maximized);

/// <summary>Looks at the foreground window: is it full screen, a game, maximised, and which app is it?</summary>
internal static class FullscreenWatcher
{
    // Folders games are installed in by the big stores and launchers.
    private static readonly string[] GameFolders =
    [
        @"\steamapps\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\EA Games\",
        @"\Ubisoft Game Launcher\games\", @"\Battle.net\", @"\WindowsApps\Microsoft.Minecraft", @"\Roblox\",
    ];

    private static readonly string[] JavaGames = ["Minecraft", "Roblox"];

    public static ForegroundInfo Inspect(ICollection<IntPtr> ownWindows)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || ownWindows.Contains(fg) || fg == GetShellWindow() || fg == GetDesktopWindow())
            return default;
        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var name = new char[64];
        var len = GetClassNameW(fg, name, name.Length);
        var cls = new string(name, 0, len);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return default;

        GetWindowThreadProcessId(fg, out var pid);
        var path = ProcessPath(pid);
        var process = Path.GetFileNameWithoutExtension(path);

        SHQueryUserNotificationState(out var state);
        var d3d = state == QUNS_RUNNING_D3D_FULL_SCREEN;
        var fullscreen = d3d || state is QUNS_BUSY or QUNS_PRESENTATION_MODE;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var maximized = false;
        if (GetWindowRect(fg, out var r) && GetMonitorInfoW(monitor, ref info))
        {
            var m = info.rcMonitor;
            // Borderless "fullscreen" (browsers, video players) covers the monitor exactly.
            if (r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom) fullscreen = true;
            // Maximised windows overhang the work area by their border and reach its top.
            maximized = !fullscreen && r.Top <= info.rcWork.Top && r.Left <= info.rcWork.Left && r.Right >= info.rcWork.Right;
        }

        var game = fullscreen && (d3d || GameFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))
                                  || (process.Equals("javaw", StringComparison.OrdinalIgnoreCase) && JavaGames.Any(g => WindowTitle(fg).Contains(g))));
        return new ForegroundInfo(monitor, process, fullscreen, game, maximized);
    }

    /// <summary>Whether the top window on a monitor covers it: maximised (fills the work area) or full screen (the whole monitor).</summary>
    internal readonly record struct Cover(bool Fullscreen, bool Maximized);

    /// <summary>
    /// For each monitor, the state of its topmost ordinary window, so every screen's island can slim
    /// over its own maximised or full-screen window — not only the screen the active window is on.
    /// </summary>
    public static Dictionary<IntPtr, Cover> CoverByMonitor(ICollection<IntPtr> ownWindows)
    {
        var result = new Dictionary<IntPtr, Cover>();
        var shell = GetShellWindow();
        var desktop = GetDesktopWindow();
        var name = new char[64];
        EnumWindows((hwnd, _) =>
        {
            if (ownWindows.Contains(hwnd) || hwnd == shell || hwnd == desktop) return true;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            // Cloaked windows (another virtual desktop, suspended UWP) are not really on screen.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var len = GetClassNameW(hwnd, name, name.Length);
            var cls = new string(name, 0, len);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (result.ContainsKey(monitor)) return true; // the topmost window for this monitor wins (EnumWindows is front-to-back)
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetWindowRect(hwnd, out var r) || !GetMonitorInfoW(monitor, ref info)) return true;
            var m = info.rcMonitor;
            var fullscreen = r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
            var maximized = !fullscreen && r.Top <= info.rcWork.Top && r.Left <= info.rcWork.Left && r.Right >= info.rcWork.Right;
            result[monitor] = new Cover(fullscreen, maximized);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private const int DWMWA_CLOAKED = 14;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private static string WindowTitle(IntPtr hwnd) => Native.WindowText(hwnd);

    private static string ProcessPath(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
