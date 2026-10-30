using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace HaloBar.Services;

/// <summary>Reads what a snipped picture says: its text (Windows' own OCR, on this PC) or a QR code in it.</summary>
internal static class ScreenReader
{
    /// <summary>The picture's text, line by line, or null when there's none. Call off the window thread.</summary>
    public static async Task<string?> TextAsync(byte[] png)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Small snips read better made bigger; OCR has its own size limit.
        var scale = Math.Clamp(1200.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight), 1, 3);
        scale = Math.Min(scale, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The text of the QR code (or other barcode) in the picture, or null.</summary>
    public static string? Code(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap == null) return null;
        using var rgba = bitmap.Copy(SKColorType.Rgba8888) ?? bitmap;
        var source = new RGBLuminanceSource(rgba.Bytes, rgba.Width, rgba.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        var reader = new MultiFormatReader();
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true };
        try
        {
            return reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints)?.Text
                   // A light code on a dark background (dark-mode apps).
                   ?? reader.decode(new BinaryBitmap(new HybridBinarizer(new InvertedLuminanceSource(source))), hints)?.Text;
        }
        catch (ReaderException)
        {
            return null;
        }
    }
}
