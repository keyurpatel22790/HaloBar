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
    private bool _typing, _pressed, _dragging;
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
            case WM_WINDOWPOSCHANGING:
                // Whatever else changes, stay under every other window, on the desktop.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.hwndInsertAfter = HWND_BOTTOM;
                pos.flags &= ~SWP_NOZORDER;
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
                if (_dragging)
                    SetWindowPos(Handle, HWND_BOTTOM, _startRect.Left + now.X - _pressAt.X, _startRect.Top + now.Y - _pressAt.Y, 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE);
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
}
