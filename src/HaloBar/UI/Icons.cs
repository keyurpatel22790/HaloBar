using SkiaSharp;

namespace HaloBar.UI;

/// <summary>Small vector glyphs, drawn centred on (cx, cy) at roughly <c>size</c> across.</summary>
internal static class Icons
{
    public static void Play(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var path = new SKPathBuilder();
        var h = size * 0.5f;
        path.MoveTo(cx - h * 0.7f, cy - h);
        path.LineTo(cx + h, cy);
        path.LineTo(cx - h * 0.7f, cy + h);
        path.Close();
        Fill(c, path, color);
    }

    public static void Pause(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var h = size * 0.5f;
        var w = size * 0.26f;
        using var paint = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx - h * 0.8f, cy - h, cx - h * 0.8f + w, cy + h), 1.5f, 1.5f, paint);
        c.DrawRoundRect(new SKRect(cx + h * 0.8f - w, cy - h, cx + h * 0.8f, cy + h), 1.5f, 1.5f, paint);
    }

    public static void Next(SKCanvas c, float cx, float cy, float size, SKColor color) => Skip(c, cx, cy, size, color, 1);
    public static void Previous(SKCanvas c, float cx, float cy, float size, SKColor color) => Skip(c, cx, cy, size, color, -1);

    private static void Skip(SKCanvas c, float cx, float cy, float size, SKColor color, int dir)
    {
        var h = size * 0.42f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx - dir * h, cy - h);
        path.LineTo(cx + dir * h * 0.6f, cy);
        path.LineTo(cx - dir * h, cy + h);
        path.Close();
        Fill(c, path, color);
        using var paint = FillPaint(color);
        var bx = cx + dir * h * 0.75f;
        c.DrawRoundRect(new SKRect(bx - size * 0.07f, cy - h, bx + size * 0.07f, cy + h), 1, 1, paint);
    }

    public static void Speaker(SKCanvas c, float cx, float cy, float size, SKColor color, float level, bool muted)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 9 * s, cy - 3.5f * s);
        path.LineTo(cx - 5 * s, cy - 3.5f * s);
        path.LineTo(cx, cy - 8 * s);
        path.LineTo(cx, cy + 8 * s);
        path.LineTo(cx - 5 * s, cy + 3.5f * s);
        path.LineTo(cx - 9 * s, cy + 3.5f * s);
        path.Close();
        Fill(c, path, color);

        using var stroke = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f * s, StrokeCap = SKStrokeCap.Round };
        if (muted)
        {
            c.DrawLine(cx + 3 * s, cy - 4 * s, cx + 9 * s, cy + 4 * s, stroke);
            c.DrawLine(cx + 9 * s, cy - 4 * s, cx + 3 * s, cy + 4 * s, stroke);
            return;
        }
        if (level > 0.01f) c.DrawArc(new SKRect(cx - 1 * s, cy - 4 * s, cx + 7 * s, cy + 4 * s), -55, 110, false, stroke);
        if (level > 0.5f) c.DrawArc(new SKRect(cx - 3 * s, cy - 8 * s, cx + 11 * s, cy + 8 * s), -55, 110, false, stroke);
    }

    public static void Bell(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 7 * s, cy + 5 * s);
        path.CubicTo(cx - 5 * s, cy + 2 * s, cx - 6 * s, cy - 7 * s, cx, cy - 7 * s);
        path.CubicTo(cx + 6 * s, cy - 7 * s, cx + 5 * s, cy + 2 * s, cx + 7 * s, cy + 5 * s);
        path.Close();
        Fill(c, path, color);
        using var paint = FillPaint(color);
        c.DrawCircle(cx, cy + 7.5f * s, 2 * s, paint);
    }

    public static void File(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var stroke = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, StrokeJoin = SKStrokeJoin.Round };
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 6 * s, cy - 8 * s);
        path.LineTo(cx + 2 * s, cy - 8 * s);
        path.LineTo(cx + 6 * s, cy - 4 * s);
        path.LineTo(cx + 6 * s, cy + 8 * s);
        path.LineTo(cx - 6 * s, cy + 8 * s);
        path.Close();
        using var outline = path.Detach();
        c.DrawPath(outline, stroke);
    }

    private static SKPaint FillPaint(SKColor color) => new() { Color = color, IsAntialias = true };

    private static void Fill(SKCanvas c, SKPathBuilder builder, SKColor color)
    {
        using var paint = FillPaint(color);
        using var path = builder.Detach();
        c.DrawPath(path, paint);
    }
}
