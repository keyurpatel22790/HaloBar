using SkiaSharp;

namespace HaloBar.Rendering;

/// <summary>
/// Colours taken from album art: the most striking one for accents, a deep one for backgrounds,
/// and a few swatches for the music card's soft colour streaks.
/// </summary>
internal sealed record ArtPalette(SKColor Vibrant, SKColor Deep, SKColor[] Swatches)
{
    private const int HueBuckets = 12;

    public static ArtPalette? From(SKImage? image)
    {
        if (image == null) return null;
        using var small = ImageTools.Shrink(image, 24);
        using var bitmap = SKBitmap.FromImage(small);
        if (bitmap == null) return null;

        // Group pixels by hue; weight colourful, bright pixels more than greys and near-blacks.
        var weight = new float[HueBuckets];
        var sum = new (float R, float G, float B)[HueBuckets];
        float totalR = 0, totalG = 0, totalB = 0;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            c.ToHsv(out var h, out var s, out var v);
            totalR += c.Red; totalG += c.Green; totalB += c.Blue;
            count++;
            var w = s / 100f * (v / 100f) * (v > 15 ? 1 : 0);
            if (w < 0.05f) continue;
            var b = (int)(h / 360f * HueBuckets) % HueBuckets;
            weight[b] += w;
            sum[b].R += c.Red * w; sum[b].G += c.Green * w; sum[b].B += c.Blue * w;
        }
        if (count == 0) return null;

        var average = new SKColor((byte)(totalR / count), (byte)(totalG / count), (byte)(totalB / count));
        var ranked = Enumerable.Range(0, HueBuckets).Where(b => weight[b] > 0).OrderByDescending(b => weight[b]).ToList();
        var swatches = ranked.Take(3)
            .Select(b => new SKColor((byte)(sum[b].R / weight[b]), (byte)(sum[b].G / weight[b]), (byte)(sum[b].B / weight[b])))
            .ToList();
        if (swatches.Count == 0) swatches.Add(average); // a grey or black-and-white cover
        while (swatches.Count < 3) swatches.Add(Shade(swatches[^1], 0.85f, 0.7f));

        return new ArtPalette(Vivid(swatches[0]), Darken(swatches[0]), [.. swatches]);
    }

    /// <summary>Bright and saturated enough to read as an accent on a dark island.</summary>
    private static SKColor Vivid(SKColor c)
    {
        c.ToHsv(out var h, out var s, out var v);
        return SKColor.FromHsv(h, Math.Max(s, s < 8 ? 0 : 55), Math.Max(v, 88));
    }

    /// <summary>Dark enough for light text to read on top of it.</summary>
    private static SKColor Darken(SKColor c)
    {
        c.ToHsv(out var h, out var s, out _);
        return SKColor.FromHsv(h, Math.Min(s, 70), 16);
    }

    private static SKColor Shade(SKColor c, float saturation, float value)
    {
        c.ToHsv(out var h, out var s, out var v);
        return SKColor.FromHsv(h, s * saturation, v * value);
    }
}
