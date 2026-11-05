using System.Runtime.InteropServices;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>
/// A software screen dimmer and warmth: a click-through, always-on-top layer over every monitor,
/// tinted dark (dim) and amber (warmth). It works even on screens with no hardware brightness
/// (desktop monitors, the external panel). Kept out of captures, so screenshots stay true colour.
/// </summary>
internal sealed class DimOverlay : NativeWindow
{
    private const int Ex = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
    private const uint WM_PAINT = 0x000F;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private uint _colour; // GDI 0x00BBGGRR
    private bool _shown;

    public DimOverlay() : base("HaloBar.Dim", Ex, WS_POPUP)
    {
        SetWindowDisplayAffinity(Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
    }

    /// <summary>
    /// Sets the look: <paramref name="dim"/> and <paramref name="warmth"/> are 0–1. Dim darkens;
    /// warmth tints amber and reduces blue. Both 0 hides the layer.
    /// </summary>
    public void Set(float dim, float warmth)
    {
        dim = Math.Clamp(dim, 0, 0.9f);
        warmth = Math.Clamp(warmth, 0, 1f);
        // Amber tint carries the warmth; its coverage adds a little to the dim.
        var alpha = Math.Clamp(dim + warmth * 0.28f, 0, 0.92f);
        if (alpha <= 0.001f)
        {
            if (_shown) { ShowWindow(Handle, 0 /* SW_HIDE */); _shown = false; }
            return;
        }
        byte r = (byte)(warmth * 90), g = (byte)(warmth * 45), b = 0;
        _colour = (uint)(b << 16 | g << 8 | r);
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        SetWindowPos(Handle, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetLayeredWindowAttributes(Handle, 0, (byte)(alpha * 255), LWA_ALPHA);
        _shown = true;
        InvalidateRect(Handle, IntPtr.Zero, true);
    }

    protected override IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_PAINT) return null;
        var hdc = BeginPaint(Handle, out var ps);
        if (GetClientRect(Handle, out var rc))
        {
            var brush = CreateSolidBrush(_colour);
            FillRect(hdc, ref rc, brush);
            DeleteObject(brush);
        }
        EndPaint(Handle, ref ps);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr Hdc; public int Erase; public RECT Paint; public int Restore; public int IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
