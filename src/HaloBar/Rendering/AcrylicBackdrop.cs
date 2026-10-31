using System.Numerics;
using System.Runtime.InteropServices;
using WinRT;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace HaloBar.Rendering;

/// <summary>
/// The blurred desktop behind the island for the Acrylic surfaces, the way Windows 11's own menus
/// look. A Windows.UI.Composition visual with a host-backdrop brush sits behind the swap chain (a
/// second, non-topmost composition target on the same window) and is clipped to the island's
/// outline every frame; the island then draws a translucent tint over it.
/// </summary>
internal sealed class AcrylicBackdrop : IDisposable
{
    private static Compositor? _compositor;
    private static object? _queueController;

    private readonly DesktopWindowTarget _target;
    private readonly SpriteVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _clip;

    /// <summary>Sets up composition on the calling (window) thread. False when the system can't.</summary>
    public static bool Initialize()
    {
        if (_compositor != null) return true;
        try
        {
            // Windows.UI.Composition needs a dispatcher queue on this thread; the frame loop pumps it.
            var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
            if (CreateDispatcherQueueController(options, out var controller) != 0) return false;
            _queueController = controller;
            _compositor = new Compositor();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Acrylic: composition unavailable: {ex.Message}");
            return false;
        }
    }

    public static AcrylicBackdrop? Create(IntPtr hwnd)
    {
        if (!Initialize()) return null;
        // A window only gets a live host backdrop (rather than black) once it asks DWM for one.
        var on = 1;
        DwmSetWindowAttribute(hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, sizeof(int));
        try { return new AcrylicBackdrop(hwnd); }
        catch (Exception ex)
        {
            Log.Write($"Acrylic: {ex.Message}");
            return null;
        }
    }

    private AcrylicBackdrop(IntPtr hwnd)
    {
        var c = _compositor!;
        var interop = c.As<ICompositorDesktopInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(hwnd, false, out var raw));
        _target = DesktopWindowTarget.FromAbi(raw);
        Marshal.Release(raw);
        _visual = c.CreateSpriteVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        _visual.Brush = c.CreateHostBackdropBrush();
        _clip = c.CreateRoundedRectangleGeometry();
        _visual.Clip = c.CreateGeometricClip(_clip);
        _visual.Opacity = 0;
        _target.Root = _visual;
    }

    /// <summary>Shows the blur inside the island's outline (window pixels), or hides it.</summary>
    public void Update(bool on, float left, float top, float width, float height, float radius)
    {
        _visual.Opacity = on ? 1 : 0;
        if (!on) return;
        _clip.Offset = new Vector2(left, top);
        _clip.Size = new Vector2(width, height);
        _clip.CornerRadius = new Vector2(radius, radius);
    }

    public void Dispose()
    {
        _target.Root = null;
        _visual.Dispose();
        _target.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;    // DQTYPE_THREAD_CURRENT
        public int apartmentType; // DQTAT_COM_STA
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
