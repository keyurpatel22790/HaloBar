using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using static HaloBar.Platform.Native;

namespace HaloBar.Platform;

/// <summary>
/// What one clipboard read produced. Exactly one of Text, Files or Image is set. Pictures keep their
/// PNG encoding (small) for copying back; <see cref="Image"/> may be a full-size decode or a thumbnail.
/// </summary>
internal sealed record ClipboardContent(string? Text, string[]? Files, SKImage? Image, byte[]? Png);

/// <summary>Raw Win32 clipboard access: reading text, files and pictures, and putting them back.</summary>
internal static class ClipboardIO
{
    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");
    // Password managers and other private sources mark their copies with these formats.
    private static readonly uint ExcludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint ViewerIgnoreFormat = RegisterClipboardFormatW("Clipboard Viewer Ignore");

    public static ClipboardContent? Read(IntPtr owner)
    {
        if (!Open(owner)) return null;
        try
        {
            if (IsClipboardFormatAvailable(ExcludeFormat) || IsClipboardFormatAvailable(ViewerIgnoreFormat)) return null;
            if (IsClipboardFormatAvailable(HistoryFormat) && ReadBytes(HistoryFormat) is { Length: >= 4 } flag
                && BitConverter.ToInt32(flag) == 0) return null;

            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFiles();
                if (files.Length > 0) return new ClipboardContent(null, files, null, null);
            }
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadText();
                if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent(text, null, null, null);
            }
            byte[]? png = IsClipboardFormatAvailable(PngFormat) ? ReadBytes(PngFormat) : null;
            byte[]? dib = IsClipboardFormatAvailable(CF_DIB) ? ReadBytes(CF_DIB) : null;
            SKImage? image = png != null ? SKImage.FromEncodedData(png) : null;
            if (image == null && dib != null) image = DecodeDib(dib);
            return image == null ? null : new ClipboardContent(null, null, image, png);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool Write(IntPtr owner, ClipboardContent content)
    {
        if (!Open(owner)) return false;
        try
        {
            EmptyClipboard();
            if (content.Text != null)
                return Put(CF_UNICODETEXT, Encoding.Unicode.GetBytes(content.Text + "\0"));
            if (content.Files != null)
                return Put(CF_HDROP, DropFiles(content.Files));
            if (content.Png == null) return false;
            // Most apps paste a DIB; browsers and chat apps prefer PNG. Offer both.
            var ok = PngToDib(content.Png) is { } dib && Put(CF_DIB, dib);
            return Put(PngFormat, content.Png) | ok;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another app may hold the clipboard for a moment; try a few times.</summary>
    private static bool Open(IntPtr owner)
    {
        for (var i = 0; i < 5; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var ptr = GlobalLock(handle);
        try { return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr); }
        finally { GlobalUnlock(handle); }
    }

    private static string[] ReadFiles()
    {
        var drop = GetClipboardData(CF_HDROP);
        if (drop == IntPtr.Zero) return [];
        var count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
        var files = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            var len = DragQueryFileW(drop, i, null, 0);
            var buffer = new char[len + 1];
            DragQueryFileW(drop, i, buffer, len + 1);
            files.Add(new string(buffer, 0, (int)len));
        }
        return [.. files];
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)GlobalSize(handle);
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool Put(uint format, byte[] bytes)
    {
        var mem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (mem == IntPtr.Zero) return false;
        var ptr = GlobalLock(mem);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) != IntPtr.Zero) return true;
        GlobalFree(mem); // only freed on failure: on success the clipboard owns it
        return false;
    }

    /// <summary>DROPFILES header followed by a double-null-terminated list of wide paths.</summary>
    private static byte[] DropFiles(string[] files)
    {
        var list = Encoding.Unicode.GetBytes(string.Join("\0", files) + "\0\0");
        var bytes = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);   // pFiles: offset of the list
        BitConverter.GetBytes(1).CopyTo(bytes, 16);   // fWide: paths are UTF-16
        list.CopyTo(bytes, 20);
        return bytes;
    }

    /// <summary>A bottom-up 32-bit BI_RGB DIB of a PNG picture.</summary>
    private static byte[]? PngToDib(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        if (decoded == null) return null;
        using var bitmap = decoded.Copy(SKColorType.Bgra8888);
        var (w, h) = (bitmap.Width, bitmap.Height);
        var dib = new byte[40 + w * h * 4];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);   // positive height: rows bottom-up
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(w * h * 4).CopyTo(dib, 20);
        var pixels = bitmap.GetPixelSpan();
        var stride = w * 4;
        for (var y = 0; y < h; y++)
            pixels.Slice(y * bitmap.RowBytes, stride).CopyTo(dib.AsSpan(40 + (h - 1 - y) * stride, stride));
        return dib;
    }

    /// <summary>
    /// Decodes a packed DIB. 24/32-bit pictures (screenshots) are converted directly, treating them
    /// as opaque, because their alpha byte is usually zero; anything else goes through Skia's BMP codec.
    /// </summary>
    private static SKImage? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var height = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colorsUsed = BitConverter.ToUInt32(dib, 32);

        var masks = headerSize == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        var palette = bitCount <= 8 ? (int)(colorsUsed == 0 ? 1u << bitCount : colorsUsed) * 4 : (int)colorsUsed * 4;
        var pixelOffset = headerSize + masks + palette;

        if ((bitCount == 32 || bitCount == 24) && (compression == 0 || compression == 3) && width > 0 && width < 20000)
        {
            var bottomUp = height > 0;
            var h = Math.Abs(height);
            var stride = ((width * bitCount + 31) / 32) * 4;
            if (pixelOffset + (long)stride * h > dib.Length) return null;
            using var bitmap = new SKBitmap(new SKImageInfo(width, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var dest = bitmap.GetPixelSpan();
            var bpp = bitCount / 8;
            for (var y = 0; y < h; y++)
            {
                var src = pixelOffset + (bottomUp ? h - 1 - y : y) * stride;
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var s = src + x * bpp;
                    var d = row + x * 4;
                    dest[d] = dib[s];
                    dest[d + 1] = dib[s + 1];
                    dest[d + 2] = dib[s + 2];
                    dest[d + 3] = 255;
                }
            }
            return SKImage.FromBitmap(bitmap);
        }

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + pixelOffset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        return SKImage.FromEncodedData(file);
    }
}
