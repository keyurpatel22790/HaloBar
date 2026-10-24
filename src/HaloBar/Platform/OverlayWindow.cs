using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>
/// Registers window classes once and routes each window's messages to its owning object.
/// One class procedure serves every window of the class, so it is looked up by handle.
/// </summary>
internal abstract class NativeWindow : IDisposable
{
    private static readonly Dictionary<IntPtr, NativeWindow> Windows = [];
    private static readonly HashSet<string> Registered = [];
    // The class procedure must outlive every window, or the GC frees the thunk Windows calls.
    private static readonly WndProc ClassProc = Dispatch;

    public IntPtr Handle { get; }

    protected NativeWindow(string className, int exStyle, uint style)
    {
        var hInstance = GetModuleHandleW(null);
        if (Registered.Add(className))
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassProc),
                hInstance = hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = className,
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }
        Handle = CreateWindowExW(exStyle, className, "HaloBar", style, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Windows[Handle] = this;
    }

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window) && window.OnMessage(msg, wParam, lParam) is { } result)
                return result;
        }
        catch (Exception ex)
        {
            // An exception must not unwind through native frames.
            Log.Write($"WndProc 0x{msg:X}: {ex}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Handles a message; null passes it to DefWindowProc.</summary>
    protected abstract IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam);

    public virtual void Dispose()
    {
        Windows.Remove(Handle);
        DestroyWindow(Handle);
    }
}

/// <summary>
/// A hidden window that is never shown: it owns the tray icon, the hotkey and the clipboard
/// listener, and hears system-wide broadcasts (display changes, Explorer restarting) once for the app.
/// </summary>
internal sealed class HostWindow() : NativeWindow("HaloBar.Host", WS_EX_TOOLWINDOW, WS_POPUP)
{
    public Func<uint, IntPtr, IntPtr, bool>? MessageHook;
    public event Action? DisplayChanged;
    public event Action? Closed;

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
            case 0x1A: // WM_SETTINGCHANGE: work area and scaling changes arrive this way
                DisplayChanged?.Invoke();
                break;
            case WM_DESTROY:
                Closed?.Invoke();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return MessageHook?.Invoke(msg, wParam, lParam) == true ? IntPtr.Zero : null;
    }
}

/// <summary>
/// The always-on-top window one island is drawn into, at the top of one monitor. It never takes
/// focus, has no taskbar button, and lets clicks fall through to whatever is below unless the
/// pointer is on the island. Its pixels come from a DirectComposition visual, so there is no GDI
/// redirection surface.
/// </summary>
internal sealed class OverlayWindow : NativeWindow
{
    private const int ExStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT
                                | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;

    private readonly int _widthDip, _heightDip;
    private bool _clickThrough = true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float Scale { get; private set; } = 1f;
    public MonitorInfo Monitor { get; private set; }

    /// <summary>Window-relative pixel coordinates.</summary>
    public event Action<int, int>? MouseUp;
    public event Action<int>? MouseWheel;
    public event Action? DpiChanged;

    public OverlayWindow(int widthDip, int heightDip, MonitorInfo monitor) : base("HaloBar.Overlay", ExStyle, WS_POPUP)
    {
        _widthDip = widthDip;
        _heightDip = heightDip;
        Monitor = monitor;
        // A layered window stays invisible until it has an alpha; the real alpha comes from the swap chain.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
        Place(monitor);
        ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
    }

    /// <summary>Centres the window at the top of <paramref name="monitor"/>, sized for its DPI.</summary>
    public void Place(MonitorInfo monitor)
    {
        Monitor = monitor;
        Scale = monitor.Scale;
        Width = (int)Math.Ceiling(_widthDip * Scale);
        Height = (int)Math.Ceiling(_heightDip * Scale);
        var bounds = monitor.Bounds;
        var x = bounds.Left + (bounds.Width - Width) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, bounds.Top, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Puts the window back above other topmost windows (some apps push themselves over it).</summary>
    public void KeepOnTop() => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>When true, the pointer goes straight through to the windows below.</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;
            var style = (long)GetWindowLongPtrW(Handle, GWL_EXSTYLE);
            style = value ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
            SetWindowLongPtrW(Handle, GWL_EXSTYLE, new IntPtr(style));
        }
    }

    public void SetCaptureExcluded(bool excluded) =>
        SetWindowDisplayAffinity(Handle, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    /// <summary>Pointer position relative to the window, in physical pixels.</summary>
    public (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        GetWindowRect(Handle, out var r);
        return (p.X - r.Left, p.Y - r.Top);
    }

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                return new IntPtr(1);
            case WM_LBUTTONUP:
                MouseUp?.Invoke(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                MouseWheel?.Invoke(HiWord(wParam));
                return IntPtr.Zero;
            case WM_DPICHANGED:
                DpiChanged?.Invoke();
                return IntPtr.Zero;
        }
        return null;
    }
}
