using SkiaSharp;

namespace HaloBar.Rendering;

/// <summary>
/// Glass while the island has to show in recordings and screen shares. It can't be kept out of
/// captures then, so a copy of the screen has the island itself where the glass needs what's
/// behind it. That hole is filled from the screen around it, its colours grown inward (pull-push:
/// averaged down to a few pixels, then spread back up into the gap), and blurred with the rest.
/// Glass is soft anyway, so it still takes on the colours behind it and follows them as they change.
/// </summary>
internal static class GlassFill
{
    /// <summary>The fill is worked out at about this many pixels across; it's blurred, so detail doesn't matter.</summary>
    private const int WorkWidth = 96;

    /// <summary>
    /// <paramref name="sharp"/> with <paramref name="hole"/> (its pixels) filled from around it, or
    /// itself when nothing around it is left to fill from.
    /// </summary>
    public static SKImage Fill(SKImage sharp, SKRectI hole)
    {
        hole = SKRectI.Intersect(hole, new SKRectI(0, 0, sharp.Width, sharp.Height));
        if (hole.IsEmpty) return sharp;
        var scale = Math.Min(1f, WorkWidth / (float)sharp.Width);
        int w = Math.Max(2, (int)(sharp.Width * scale)), h = Math.Max(2, (int)(sharp.Height * scale));

        // The picture small, the hole see-through (premultiplied, so averaging leaves it out).
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var work = new SKBitmap(info);
        using (var canvas = new SKCanvas(work))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(sharp, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
            using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
            canvas.DrawRect(new SKRect(hole.Left * scale - 1, hole.Top * scale - 1, hole.Right * scale + 1, hole.Bottom * scale + 1), clear);
        }
        var level = new float[w * h * 4];
        var bytes = work.GetPixelSpan();
        for (var i = 0; i < level.Length; i++) level[i] = bytes[i] / 255f;
        if (!PullPush(level, w, h)) return sharp;
        var filled = work.GetPixelSpan();
        unsafe
        {
            var target = (byte*)work.GetPixels();
            for (var i = 0; i < level.Length; i++) target[i] = (byte)Math.Clamp(level[i] * 255 + 0.5f, 0, 255);
        }

        // The filled picture under the real one, the hole cut out of the real one with a soft edge.
        using var surface = SKSurface.Create(new SKImageInfo(sharp.Width, sharp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        using (var fill = SKImage.FromBitmap(work))
        using (var soft = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(1.5f / scale, 1.5f / scale, SKShaderTileMode.Clamp) })
            c.DrawImage(fill, new SKRect(0, 0, sharp.Width, sharp.Height), new SKSamplingOptions(SKFilterMode.Linear), soft);
        c.Save();
        c.ClipRect(hole, SKClipOperation.Difference);
        c.DrawImage(sharp, 0, 0);
        c.Restore();
        return surface.Snapshot();
    }

    /// <summary>
    /// Fills the see-through pixels of a premultiplied RGBA picture from the opaque ones around
    /// them. False when there's nothing opaque to fill from.
    /// </summary>
    internal static bool PullPush(float[] px, int w, int h)
    {
        if (w <= 1 && h <= 1) return px[3] > 0.001f;
        int cw = Math.Max(1, (w + 1) / 2), ch = Math.Max(1, (h + 1) / 2);
        var coarse = new float[cw * ch * 4];
        // Pull: each coarse pixel is the average of the opaque part of the 2×2 below it.
        for (var y = 0; y < ch; y++)
        for (var x = 0; x < cw; x++)
        {
            float r = 0, g = 0, b = 0, a = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy), i = (sy * w + sx) * 4;
                r += px[i]; g += px[i + 1]; b += px[i + 2]; a += px[i + 3];
            }
            var o = (y * cw + x) * 4;
            if (a > 0.0001f)
            {
                // Colour of what was there, as opaque as it was (up to fully).
                var keep = Math.Min(1, a);
                coarse[o] = r / a * keep; coarse[o + 1] = g / a * keep; coarse[o + 2] = b / a * keep; coarse[o + 3] = keep;
            }
        }
        if (!PullPush(coarse, cw, ch)) return false;
        // Push: what's missing here comes from the (now whole) coarser level.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var missing = 1 - px[i + 3];
            if (missing <= 0.0001f) continue;
            var o = (Math.Min(ch - 1, y / 2) * cw + Math.Min(cw - 1, x / 2)) * 4;
            px[i] += missing * coarse[o]; px[i + 1] += missing * coarse[o + 1]; px[i + 2] += missing * coarse[o + 2]; px[i + 3] = 1;
        }
        return true;
    }
}
