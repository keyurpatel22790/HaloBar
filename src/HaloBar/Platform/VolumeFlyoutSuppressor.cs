using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>
/// Keeps Windows' own volume pop-up out of sight so only the island's HUD shows.
///
/// Windows 11 draws the pop-up in a small borderless XAML host window owned by Explorer
/// (class XamlExplorerHostIslandWindow, no title) that sits above the taskbar; Windows 10 uses
/// NativeHWNDHost with a DirectUIHWND child. Explorer shows and hides the same window each
/// time, so giving it an empty window region keeps it invisible until the region is removed.
/// Minimising it, the old Windows 10 trick, no longer works on Windows 11.
/// Explorer only creates the window the first time the pop-up is needed, so until then this
/// keeps looking whenever the volume changes.
/// </summary>
internal sealed class VolumeFlyoutSuppressor : IDisposable
{
    private IntPtr _held;
    private bool _reportedMissing;

    public bool IsHeld => _held != IntPtr.Zero && IsWindow(_held);

    /// <summary>Finds and hides the pop-up if it isn't hidden already. Cheap when it is.</summary>
    public void Ensure()
    {
        if (IsHeld) return;
        _held = IntPtr.Zero;
        var (window, what) = Find();
        if (window == IntPtr.Zero)
        {
            if (!_reportedMissing) Log.Write("Volume pop-up: not created yet; looking again when the volume changes");
            _reportedMissing = true;
            return;
        }
        var empty = CreateRectRgn(0, 0, 0, 0);
        if (SetWindowRgn(window, empty, true) == 0)
        {
            DeleteObject(empty); // only on failure: on success the window owns the region
            Log.Write($"Volume pop-up: found {what} but could not hide it");
            return;
        }
        _held = window;
        _reportedMissing = false;
        Log.Write($"Volume pop-up: hidden ({what})");
    }

    /// <summary>Gives the pop-up back to Windows.</summary>
    public void Release()
    {
        if (IsHeld)
        {
            SetWindowRgn(_held, IntPtr.Zero, true);
            Log.Write("Volume pop-up: given back to Windows");
        }
        _held = IntPtr.Zero;
    }

    /// <summary>Explorer restarted: its windows are new.</summary>
    public void Reset() => _held = IntPtr.Zero;

    private static (IntPtr Window, string What) Find()
    {
        GetWindowThreadProcessId(GetShellWindow(), out var explorer);

        IntPtr after = IntPtr.Zero;
        while ((after = FindWindowExW(IntPtr.Zero, after, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(after, out var pid);
            if (pid != explorer || WindowText(after).Length > 0) continue;
            // The pop-up host is pill-sized; other untitled XAML hosts (Start, task view) span the screen.
            if (GetWindowRect(after, out var r) && r.Width is > 0 and < 900 && r.Height is > 0 and < 300)
                return (after, $"Windows 11 XAML host {r.Width}x{r.Height} at ({r.Left},{r.Top})");
        }

        var host = IntPtr.Zero;
        while ((host = FindWindowExW(IntPtr.Zero, host, "NativeHWNDHost", "")) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(host, out var pid);
            if (pid == explorer && FindWindowExW(host, IntPtr.Zero, "DirectUIHWND", "") != IntPtr.Zero)
                return (host, "Windows 10 NativeHWNDHost");
        }
        return (IntPtr.Zero, "");
    }

    public void Dispose() => Release();
}
