using SkiaSharp;

namespace HaloBar.Rendering;

internal static class ImageTools
{
    /// <summary>
    /// A decoded, in-memory copy no larger than <paramref name="maxSide"/> pixels on its longest side.
    /// Album art and screenshots arrive at full size but are drawn at 20–120 DIPs; keeping them
    /// small saves megabytes each, and a raster image skips Skia's lazy-decode cache.
    /// </summary>
    public static SKImage Shrink(SKImage image, int maxSide)
    {
        var scale = Math.Min(1f, (float)maxSide / Math.Max(image.Width, image.Height));
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var pixmap = bitmap.PeekPixels())
            image.ScalePixels(pixmap, new SKSamplingOptions(SKCubicResampler.Mitchell));
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Decodes encoded bytes straight to a small raster image, or null if they aren't a picture.</summary>
    public static SKImage? DecodeSmall(byte[] encoded, int maxSide)
    {
        using var full = SKImage.FromEncodedData(encoded);
        return full == null ? null : Shrink(full, maxSide);
    }

    public static byte[] EncodePng(SKImage image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
