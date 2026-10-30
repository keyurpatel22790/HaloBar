using SkiaSharp;

namespace HaloBar.Platform;

/// <summary>
/// HaloBar's app icon, drawn in code: a dark rounded tile with the island's pill and its accent
/// dot. <see cref="WriteIco"/> saves it as a multi-size .ico for the exe and the installer.
/// </summary>
internal static class IconArt
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static void Draw(SKCanvas c, float size)
    {
        var s = size / 256f;
        c.Clear(SKColors.Transparent);
        // The tile: near-black with a soft top light, like the island's surface.
        var tile = new SKRect(8 * s, 8 * s, 248 * s, 248 * s);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, tile.Top), new SKPoint(0, tile.Bottom),
                   [new SKColor(0x2A, 0x2D, 0x36), new SKColor(0x0B, 0x0C, 0x10)], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            c.DrawRoundRect(tile, 56 * s, 56 * s, paint);
        using (var rim = new SKPaint { Color = SKColors.White.WithAlpha(28), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, 3 * s) })
            c.DrawRoundRect(Inset(tile, 1.5f * s), 55 * s, 55 * s, rim);

        // The pill.
        var pill = new SKRect(40 * s, 98 * s, 216 * s, 158 * s);
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            c.DrawRoundRect(pill, 30 * s, 30 * s, paint);
        using (var glow = new SKPaint { Color = new SKColor(0x7C, 0x9C, 0xFF).WithAlpha(70), IsAntialias = true,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10 * s) })
            c.DrawCircle(182 * s, 128 * s, 14 * s, glow);
        using (var dot = new SKPaint { Color = new SKColor(0x7C, 0x9C, 0xFF), IsAntialias = true })
            c.DrawCircle(182 * s, 128 * s, 11 * s, dot);
        // A bar of "text" in the pill, so it reads as a live activity at small sizes.
        using (var bar = new SKPaint { Color = SKColors.White.WithAlpha(225), IsAntialias = true })
            c.DrawRoundRect(new SKRect(70 * s, 121 * s, 146 * s, 135 * s), 7 * s, 7 * s, bar);
    }

    private static SKRect Inset(SKRect r, float by) => new(r.Left + by, r.Top + by, r.Right - by, r.Bottom - by);

    /// <summary>Writes an .ico with PNG images at the usual sizes (Windows reads PNG entries since Vista).</summary>
    public static void WriteIco(string path)
    {
        var images = Sizes.Select(size =>
        {
            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap)) Draw(canvas, size);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return (size, data.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);          // palette
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
