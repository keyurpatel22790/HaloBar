using System.Runtime.InteropServices;
using SkiaSharp;

namespace HaloBar.Platform;

/// <summary>
/// The picture Explorer shows for a file: a real thumbnail for photos and videos, the app icon
/// for everything else. Uses IShellItemImageFactory, the same source Explorer's views use.
/// </summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    public static SKImage? For(string path, int size, bool iconOnly = false)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0) return null;
            var factory = (IShellItemImageFactory)item;
            var flags = SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbitmap) != 0) return null;
            Marshal.ReleaseComObject(factory);
            return FromHBitmap(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) Native.DeleteObject(hbitmap);
        }
    }

    /// <summary>Copies a 32-bit top-down DIB section out of an HBITMAP.</summary>
    private static SKImage? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObjectW(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return null;
        var info = new SKImageInfo(bm.bmWidth, Math.Abs(bm.bmHeight), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var rowBytes = bm.bmWidthBytes;
        var dest = bitmap.GetPixels();
        // A positive height means a bottom-up DIB; flip it while copying.
        var bottomUp = bm.bmHeight > 0;
        for (var y = 0; y < info.Height; y++)
        {
            var src = bm.bmBits + (bottomUp ? info.Height - 1 - y : y) * rowBytes;
            unsafe { Buffer.MemoryCopy((void*)src, (void*)(dest + y * bitmap.RowBytes), bitmap.RowBytes, info.Width * 4); }
        }
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr h, int size, out BITMAP bm);
}
