using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HaloBar.Services;

/// <summary>One app window in a saved layout: which program, and where (screen pixels).</summary>
internal sealed class SavedWindow
{
    public string Exe { get; set; } = "";
    public string Title { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>A named arrangement of app windows ("Code + browser").</summary>
internal sealed class WindowLayout
{
    public string Name { get; set; } = "";
    public List<SavedWindow> Windows { get; set; } = [];
}

/// <summary>
/// Saves where the open app windows are and puts them back later. Apps that aren't running are
/// started and placed once their window shows (Store apps can't be started this way and are skipped).
/// </summary>
internal sealed class LayoutService(AppSettings settings, Action save)
{
    public IReadOnlyList<WindowLayout> Layouts => settings.WindowLayouts;

    /// <summary>Saves the windows open now as a new layout. Returns its name, or null with nothing to save.</summary>
    public string? SaveCurrent()
    {
        var windows = AppWindows().Select(w => w.Saved).ToList();
        if (windows.Count == 0) return null;
        var n = 1;
        while (settings.WindowLayouts.Any(l => l.Name == $"Layout {n}")) n++;
        var name = $"Layout {n}";
        settings.WindowLayouts.Add(new WindowLayout { Name = name, Windows = windows });
        save();
        return name;
    }

    public void Remove(WindowLayout layout)
    {
        settings.WindowLayouts.Remove(layout);
        save();
    }

    /// <summary>Puts the layout's windows back. Returns how many were placed now.</summary>
    public int Restore(WindowLayout layout)
    {
        var open = AppWindows().ToList();
        var used = new HashSet<IntPtr>();
        var placed = 0;
        var missing = new List<SavedWindow>();
        foreach (var want in layout.Windows)
        {
            // The same program; among several windows of it, the one whose title matches best.
            var match = open.Where(o => !used.Contains(o.Handle) && o.Saved.Exe.Equals(want.Exe, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.Saved.Title == want.Title).FirstOrDefault();
            if (match.Handle == IntPtr.Zero)
            {
                missing.Add(want);
                continue;
            }
            used.Add(match.Handle);
            Place(match.Handle, want);
            placed++;
        }
        foreach (var want in missing.Where(m => File.Exists(m.Exe) && !m.Exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
            _ = StartAndPlace(want);
        return placed;
    }

    private static async Task StartAndPlace(SavedWindow want)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(want.Exe) { UseShellExecute = true });
            // Wait (up to 15 s) for a window of that program to show, then move it.
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                var window = AppWindows().FirstOrDefault(o => o.Saved.Exe.Equals(want.Exe, StringComparison.OrdinalIgnoreCase));
                if (window.Handle == IntPtr.Zero) continue;
                await Task.Delay(400); // let it finish its own first placement
                Place(window.Handle, want);
                return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Layouts: couldn't start {want.Exe}: {ex.Message}");
        }
    }

    private static void Place(IntPtr hwnd, SavedWindow want)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);
        placement.showCmd = want.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        placement.normal = new RECT { Left = want.Left, Top = want.Top, Right = want.Right, Bottom = want.Bottom };
        // Window placement is in workspace coordinates; for most setups that's close to screen
        // coordinates, and SetWindowPos below corrects a normal window exactly.
        SetWindowPlacement(hwnd, ref placement);
        if (!want.Maximized)
            SetWindowPos(hwnd, IntPtr.Zero, want.Left, want.Top, want.Right - want.Left, want.Bottom - want.Top, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Visible, ordinary app windows (what Alt+Tab shows), with where each is.</summary>
    private static IEnumerable<(IntPtr Handle, SavedWindow Saved)> AppWindows()
    {
        var found = new List<(IntPtr, SavedWindow)>();
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var ex = (long)GetWindowLongPtrW(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(256);
            if (GetWindowTextW(h, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            var exe = ExePath(pid);
            if (exe == null || exe.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase) && title.ToString() == "Program Manager") return true;
            GetWindowRect(h, out var r);
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(h, ref placement);
            var maximized = placement.showCmd == SW_SHOWMAXIMIZED;
            var normal = maximized ? placement.normal : r;
            found.Add((h, new SavedWindow
            {
                Exe = exe, Title = title.ToString(), Maximized = maximized,
                Left = normal.Left, Top = normal.Top, Right = normal.Right, Bottom = normal.Bottom,
            }));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? ExePath(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = path.Capacity;
            return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const int GWL_EXSTYLE = -20, GW_OWNER = 4, DWMWA_CLOAKED = 14, SW_SHOWNORMAL = 1, SW_SHOWMAXIMIZED = 3;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT min, max;
        public RECT normal;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);
}
