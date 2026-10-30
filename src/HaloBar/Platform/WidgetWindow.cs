using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>
/// A small window on the desktop that one widget is drawn into (pixels come from a
/// DirectComposition visual, like the island's). It stays at the bottom of the window stack,
/// never takes focus unless a text box in it is being typed into, and can be dragged anywhere.
/// </summary>
internal sealed class WidgetWindow : NativeWindow
{
    private const int ExStyle = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;
    private const int DragThreshold = 4;
    private bool _typing, _pressed, _dragging, _overDesktop;
    private POINT _pressAt;
    private RECT _startRect;

    /// <summary>A click (not a drag), in window pixels.</summary>
    public event Action<int, int>? Clicked;
    /// <summary>The window was dragged to a new top-left, in screen pixels.</summary>
    public event Action<int, int>? Moved;
    public event Action? RightClicked;
    public event Action<char>? CharTyped;
    public event Action<int>? KeyPressed;
    public event Action? FocusLost;
    public event Action? DpiChanged;

    public WidgetWindow() : base("HaloBar.Widget", ExStyle, WS_POPUP)
    {
        // A layered window stays invisible until it has an alpha; the real alpha comes from the swap chain.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    public float Scale => GetDpiForWindow(Handle) / 96f;

    public void Place(int x, int y, int width, int height) =>
        SetWindowPos(Handle, HWND_BOTTOM, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public RECT Rect
    {
        get
        {
            GetWindowRect(Handle, out var r);
            return r;
        }
    }

    /// <summary>
    /// Other windows hide all of it (checked at the middle and near each corner), so drawing it
    /// would be wasted. It's drawn again as soon as any of it shows.
    /// </summary>
    public bool Covered
    {
        get
        {
            var r = Rect;
            const int inset = 6;
            POINT[] points =
            [
                new() { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 },
                new() { X = r.Left + inset, Y = r.Top + inset }, new() { X = r.Right - inset, Y = r.Top + inset },
                new() { X = r.Left + inset, Y = r.Bottom - inset }, new() { X = r.Right - inset, Y = r.Bottom - inset },
            ];
            return points.All(p => WindowFromPoint(p) != Handle);
        }
    }

    /// <summary>Pointer position relative to the window, in pixels, or null when it's elsewhere.</summary>
    public (int X, int Y)? Cursor
    {
        get
        {
            GetCursorPos(out var p);
            var r = Rect;
            return WindowFromPoint(p) == Handle ? (p.X - r.Left, p.Y - r.Top) : null;
        }
    }

    /// <summary>
    /// While the desktop itself is in front (Win+D, "Show desktop"), Windows would cover the
    /// widgets with it; lift them above until another app comes to the front.
    /// </summary>
    public void OverDesktop(bool on)
    {
        // Compare with the window's real state too, so a lift that was never undone gets undone.
        var topmost = ((long)GetWindowLongPtrW(Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        if (on == _overDesktop && on == topmost) return;
        if (AlwaysOnTop) return;
        if (on)
        {
            _overDesktop = true;
            ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            SetWindowPos(Handle, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _overDesktop = false;
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>Stays above other windows (the chat overlay) instead of on the desktop.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Puts an always-on-top window back above other topmost windows.</summary>
    public void KeepOnTop() => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public void BeginTyping()
    {
        if (_typing) return;
        _typing = true;
        var style = (long)GetWindowLongPtrW(Handle, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE;
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new IntPtr(style));
        SetForegroundWindow(Handle);
        SetFocus(Handle);
    }

    public void EndTyping()
    {
        if (!_typing) return;
        _typing = false;
        var style = (long)GetWindowLongPtrW(Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE;
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new IntPtr(style));
    }

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when !_overDesktop && !AlwaysOnTop:
                // Whatever else changes, stay under every other window, just above the desktop.
                // Not HWND_BOTTOM: that is under the desktop too, and a widget that was clicked or
                // typed into (Windows restacks it then) would vanish behind it.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                var desktop = FindWindowW("Progman", null);
                var above = desktop == IntPtr.Zero ? IntPtr.Zero : GetWindow(desktop, GW_HWNDPREV);
                if (above == Handle) pos.flags |= SWP_NOZORDER; // already right above it
                else
                {
                    pos.hwndInsertAfter = above != IntPtr.Zero ? above : HWND_BOTTOM;
                    pos.flags &= ~SWP_NOZORDER;
                }
                Marshal.StructureToPtr(pos, lParam, false);
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return new IntPtr(_typing ? MA_ACTIVATE : MA_NOACTIVATE);
            case WM_LBUTTONDOWN:
                _pressed = true;
                _dragging = false;
                GetCursorPos(out _pressAt);
                _startRect = Rect;
                SetCapture(Handle);
                return IntPtr.Zero;
            case WM_MOUSEMOVE when _pressed:
                GetCursorPos(out var now);
                if (!_dragging && Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) > DragThreshold) _dragging = true;
                // Only the position: sending it to the bottom here dropped it behind the desktop
                // while the desktop was in front (widgets are lifted above it then).
                if (_dragging)
                    SetWindowPos(Handle, IntPtr.Zero, _startRect.Left + now.X - _pressAt.X, _startRect.Top + now.Y - _pressAt.Y, 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (!_pressed) return IntPtr.Zero;
                _pressed = false;
                ReleaseCapture();
                if (_dragging)
                {
                    var r = Rect;
                    Moved?.Invoke(r.Left, r.Top);
                }
                else Clicked?.Invoke(LoWord(lParam), HiWord(lParam));
                _dragging = false;
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                RightClicked?.Invoke();
                return IntPtr.Zero;
            case WM_CHAR:
                CharTyped?.Invoke((char)(long)wParam);
                return IntPtr.Zero;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                KeyPressed?.Invoke((int)(long)wParam);
                return IntPtr.Zero;
            case WM_KILLFOCUS:
                if (_typing) FocusLost?.Invoke();
                return IntPtr.Zero;
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                return new IntPtr(1);
            case WM_DPICHANGED:
                DpiChanged?.Invoke();
                return IntPtr.Zero;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private const uint WM_WINDOWPOSCHANGING = 0x46, SWP_NOZORDER = 0x4;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    private const uint GW_HWNDPREV = 3;
}
