using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// Small vector glyphs, drawn centred on (cx, cy) at roughly <c>size</c> across. Most share one
/// signature so they can be passed around as <c>Action&lt;SKCanvas, float, float, float, SKColor&gt;</c>.
/// Outline glyphs are drawn on a 20-unit grid scaled to <c>size</c>.
/// </summary>
internal static class Icons
{
    // ───────────────────────────── media ─────────────────────────────

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

    public static void Note(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.7f * s);
        c.DrawLine(cx - 3 * s, cy + 5 * s, cx - 3 * s, cy - 7 * s, p);
        c.DrawLine(cx + 7 * s, cy + 3 * s, cx + 7 * s, cy - 9 * s, p);
        c.DrawLine(cx - 3 * s, cy - 7 * s, cx + 7 * s, cy - 9 * s, p);
        using var f = FillPaint(color);
        c.DrawOval(new SKRect(cx - 8 * s, cy + 2.5f * s, cx - 2.6f * s, cy + 7.5f * s), f);
        c.DrawOval(new SKRect(cx + 2 * s, cy + 0.5f * s, cx + 7.4f * s, cy + 5.5f * s), f);
    }

    // ───────────────────────────── HUD glyphs ─────────────────────────────

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

        using var stroke = Stroke(color, 1.8f * s);
        if (muted)
        {
            c.DrawLine(cx + 3 * s, cy - 4 * s, cx + 9 * s, cy + 4 * s, stroke);
            c.DrawLine(cx + 9 * s, cy - 4 * s, cx + 3 * s, cy + 4 * s, stroke);
            return;
        }
        if (level > 0.01f) c.DrawArc(new SKRect(cx - 1 * s, cy - 4 * s, cx + 7 * s, cy + 4 * s), -55, 110, false, stroke);
        if (level > 0.5f) c.DrawArc(new SKRect(cx - 3 * s, cy - 8 * s, cx + 11 * s, cy + 8 * s), -55, 110, false, stroke);
    }

    public static void Speaker(SKCanvas c, float cx, float cy, float size, SKColor color) => Speaker(c, cx, cy, size, color, 1, false);

    public static void Sun(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        c.DrawCircle(cx, cy, 3.6f * s, f);
        using var p = Stroke(color, 1.6f * s);
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4;
            c.DrawLine(cx + MathF.Cos(a) * 6 * s, cy + MathF.Sin(a) * 6 * s, cx + MathF.Cos(a) * 8.5f * s, cy + MathF.Sin(a) * 8.5f * s, p);
        }
    }

    public static void Moon(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var disc = new SKPathBuilder();
        disc.AddCircle(cx, cy, 7.5f * s);
        using var path = disc.Detach();
        using var biteBuilder = new SKPathBuilder();
        biteBuilder.AddCircle(cx + 4.5f * s, cy - 3.5f * s, 6.5f * s);
        using var bite = biteBuilder.Detach();
        using var moon = path.Op(bite, SKPathOp.Difference);
        using var f = FillPaint(color);
        if (moon != null) c.DrawPath(moon, f);
    }

    public static void Battery(SKCanvas c, float cx, float cy, float size, SKColor color, float level, bool charging)
    {
        var s = size / 20f;
        var body = new SKRect(cx - 9 * s, cy - 5 * s, cx + 7 * s, cy + 5 * s);
        using var p = Stroke(color, 1.4f * s);
        c.DrawRoundRect(body, 2.2f * s, 2.2f * s, p);
        using var f = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx + 7.8f * s, cy - 2 * s, cx + 9.4f * s, cy + 2 * s), 0.8f * s, 0.8f * s, f);
        var fill = level < 0.15f && !charging ? new SKColor(0xFF, 0x45, 0x3A) : color;
        using var lf = FillPaint(fill);
        var inner = new SKRect(body.Left + 2 * s, body.Top + 2 * s, body.Left + 2 * s + (body.Width - 4 * s) * Math.Clamp(level, 0.04f, 1), body.Bottom - 2 * s);
        c.DrawRoundRect(inner, 1 * s, 1 * s, lf);
        if (charging) Bolt(c, cx - 1 * s, cy, size * 0.62f, SKColors.White);
    }

    public static void Battery(SKCanvas c, float cx, float cy, float size, SKColor color) => Battery(c, cx, cy, size, color, 0.8f, false);

    public static void Bolt(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx + 1.5f * s, cy - 9 * s);
        path.LineTo(cx - 5 * s, cy + 1.5f * s);
        path.LineTo(cx - 0.5f * s, cy + 1.5f * s);
        path.LineTo(cx - 1.5f * s, cy + 9 * s);
        path.LineTo(cx + 5 * s, cy - 1.5f * s);
        path.LineTo(cx + 0.5f * s, cy - 1.5f * s);
        path.Close();
        Fill(c, path, color);
    }

    public static void Bluetooth(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.7f * s);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 5 * s, cy - 4.5f * s);
        path.LineTo(cx + 5 * s, cy + 4.5f * s);
        path.LineTo(cx, cy + 9 * s);
        path.LineTo(cx, cy - 9 * s);
        path.LineTo(cx + 5 * s, cy - 4.5f * s);
        path.LineTo(cx - 5 * s, cy + 4.5f * s);
        using var built = path.Detach();
        c.DrawPath(built, p);
    }

    public static void Wifi(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.8f * s);
        for (var i = 1; i <= 3; i++)
        {
            var r = i * 3.6f * s;
            c.DrawArc(new SKRect(cx - r, cy + 6 * s - r, cx + r, cy + 6 * s + r), -135, 90, false, p);
        }
        using var f = FillPaint(color);
        c.DrawCircle(cx, cy + 6 * s, 1.5f * s, f);
    }

    public static void Lock(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx - 6.5f * s, cy - 1.5f * s, cx + 6.5f * s, cy + 8 * s), 2 * s, 2 * s, f);
        using var p = Stroke(color, 1.8f * s);
        c.DrawArc(new SKRect(cx - 4.2f * s, cy - 8.5f * s, cx + 4.2f * s, cy + 0 * s), 180, 180, false, p);
        c.DrawLine(cx - 4.2f * s, cy - 4.2f * s, cx - 4.2f * s, cy - 1.5f * s, p);
        c.DrawLine(cx + 4.2f * s, cy - 4.2f * s, cx + 4.2f * s, cy - 1.5f * s, p);
    }

    public static void Mic(SKCanvas c, float cx, float cy, float size, SKColor color) => Mic(c, cx, cy, size, color, false);

    public static void MicOff(SKCanvas c, float cx, float cy, float size, SKColor color) => Mic(c, cx, cy, size, color, true);

    private static void Mic(SKCanvas c, float cx, float cy, float size, SKColor color, bool off)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx - 3.2f * s, cy - 9 * s, cx + 3.2f * s, cy + 2.5f * s), 3.2f * s, 3.2f * s, f);
        using var p = Stroke(color, 1.6f * s);
        c.DrawArc(new SKRect(cx - 6.5f * s, cy - 5 * s, cx + 6.5f * s, cy + 5.5f * s), 0, 180, false, p);
        c.DrawLine(cx, cy + 5.5f * s, cx, cy + 9 * s, p);
        if (off)
        {
            using var slash = Stroke(new SKColor(0xFF, 0x45, 0x3A), 2 * s);
            c.DrawLine(cx - 8 * s, cy - 8 * s, cx + 8 * s, cy + 8 * s, slash);
        }
    }

    public static void Camera(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx - 9 * s, cy - 5.5f * s, cx + 3.5f * s, cy + 5.5f * s), 2.2f * s, 2.2f * s, f);
        using var path = new SKPathBuilder();
        path.MoveTo(cx + 4.5f * s, cy - 1.5f * s);
        path.LineTo(cx + 9 * s, cy - 4.5f * s);
        path.LineTo(cx + 9 * s, cy + 4.5f * s);
        path.LineTo(cx + 4.5f * s, cy + 1.5f * s);
        path.Close();
        Fill(c, path, color);
    }

    public static void Timer(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawCircle(cx, cy + 1 * s, 7.5f * s, p);
        c.DrawLine(cx, cy + 1 * s, cx, cy - 3.5f * s, p);
        c.DrawLine(cx, cy + 1 * s, cx + 3 * s, cy + 3 * s, p);
        c.DrawLine(cx - 2.5f * s, cy - 9 * s, cx + 2.5f * s, cy - 9 * s, p);
    }

    public static void Alarm(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        Timer(c, cx, cy + 0.5f * s, size * 0.92f, color);
        using var p = Stroke(color, 1.6f * s);
        c.DrawLine(cx - 8 * s, cy - 6 * s, cx - 5 * s, cy - 9 * s, p);
        c.DrawLine(cx + 8 * s, cy - 6 * s, cx + 5 * s, cy - 9 * s, p);
    }

    public static void Check(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 2.2f * s);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 6 * s, cy);
        path.LineTo(cx - 1.5f * s, cy + 4.5f * s);
        path.LineTo(cx + 7 * s, cy - 5 * s);
        using var built = path.Detach();
        c.DrawPath(built, p);
    }

    public static void Download(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.8f * s);
        c.DrawLine(cx, cy - 8 * s, cx, cy + 3 * s, p);
        c.DrawLine(cx - 4.5f * s, cy - 1.5f * s, cx, cy + 3 * s, p);
        c.DrawLine(cx + 4.5f * s, cy - 1.5f * s, cx, cy + 3 * s, p);
        c.DrawLine(cx - 8 * s, cy + 8 * s, cx + 8 * s, cy + 8 * s, p);
    }

    public static void Drop(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx, cy - 9 * s);
        path.CubicTo(cx + 3 * s, cy - 4 * s, cx + 7 * s, cy, cx + 7 * s, cy + 3 * s);
        path.CubicTo(cx + 7 * s, cy + 7 * s, cx + 3.5f * s, cy + 9 * s, cx, cy + 9 * s);
        path.CubicTo(cx - 3.5f * s, cy + 9 * s, cx - 7 * s, cy + 7 * s, cx - 7 * s, cy + 3 * s);
        path.CubicTo(cx - 7 * s, cy, cx - 3 * s, cy - 4 * s, cx, cy - 9 * s);
        path.Close();
        Fill(c, path, color);
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

    public static void Info(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawCircle(cx, cy, 8 * s, p);
        c.DrawLine(cx, cy - 1 * s, cx, cy + 4.5f * s, p);
        using var f = FillPaint(color);
        c.DrawCircle(cx, cy - 4 * s, 1.2f * s, f);
    }

    // ───────────────────────────── panel tabs ─────────────────────────────

    public static void Home(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            c.DrawRoundRect(SKRect.Create(cx + (dx < 0 ? -7.5f : 1) * s, cy + (dy < 0 ? -7.5f : 1) * s, 6.5f * s, 6.5f * s), 1.8f * s, 1.8f * s, f);
    }

    public static void Clipboard(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawRoundRect(new SKRect(cx - 6.5f * s, cy - 6.5f * s, cx + 6.5f * s, cy + 8.5f * s), 2 * s, 2 * s, p);
        using var f = FillPaint(color);
        c.DrawRoundRect(new SKRect(cx - 3.5f * s, cy - 9 * s, cx + 3.5f * s, cy - 5 * s), 1.2f * s, 1.2f * s, f);
        c.DrawLine(cx - 3.5f * s, cy, cx + 3.5f * s, cy, p);
        c.DrawLine(cx - 3.5f * s, cy + 3.5f * s, cx + 1.5f * s, cy + 3.5f * s, p);
    }

    public static void Sliders(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        using var f = FillPaint(color);
        foreach (var (y, knob) in new[] { (-5.5f, 3f), (0f, -3f), (5.5f, 1f) })
        {
            c.DrawLine(cx - 8 * s, cy + y * s, cx + 8 * s, cy + y * s, p);
            c.DrawCircle(cx + knob * s, cy + y * s, 2.4f * s, f);
        }
    }

    public static void Palette(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawCircle(cx, cy, 8 * s, p);
        using var f = FillPaint(color);
        c.DrawCircle(cx - 3.5f * s, cy - 2.5f * s, 1.6f * s, f);
        c.DrawCircle(cx + 0.5f * s, cy - 4.5f * s, 1.6f * s, f);
        c.DrawCircle(cx + 4 * s, cy - 1.5f * s, 1.6f * s, f);
        c.DrawCircle(cx + 2 * s, cy + 4 * s, 2.2f * s, f);
    }

    public static void Grid(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        for (var y = -1; y <= 1; y++)
        for (var x = -1; x <= 1; x++)
            c.DrawCircle(cx + x * 5.5f * s, cy + y * 5.5f * s, 1.8f * s, f);
    }

    public static void Plus(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.9f * s);
        c.DrawLine(cx - 6 * s, cy, cx + 6 * s, cy, p);
        c.DrawLine(cx, cy - 6 * s, cx, cy + 6 * s, p);
    }

    /// <summary>A globe: the web panel.</summary>
    public static void Globe(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.5f * s);
        c.DrawCircle(cx, cy, 8 * s, p);
        c.DrawOval(new SKRect(cx - 3.6f * s, cy - 8 * s, cx + 3.6f * s, cy + 8 * s), p);
        c.DrawLine(cx - 8 * s, cy, cx + 8 * s, cy, p);
        c.DrawLine(cx - 6.8f * s, cy - 4 * s, cx + 6.8f * s, cy - 4 * s, p);
        c.DrawLine(cx - 6.8f * s, cy + 4 * s, cx + 6.8f * s, cy + 4 * s, p);
    }

    /// <summary>A flag on a pole: a stopwatch lap.</summary>
    public static void Flag(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawLine(cx - 5 * s, cy - 8 * s, cx - 5 * s, cy + 8 * s, p);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 5 * s, cy - 8 * s);
        path.LineTo(cx + 6 * s, cy - 5 * s);
        path.LineTo(cx - 5 * s, cy - 1 * s);
        path.Close();
        Fill(c, path, color);
    }

    /// <summary>Two opposed arrows: swap.</summary>
    public static void Swap(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawLine(cx - 7 * s, cy - 3 * s, cx + 7 * s, cy - 3 * s, p);
        c.DrawLine(cx + 7 * s, cy - 3 * s, cx + 4 * s, cy - 6 * s, p);
        c.DrawLine(cx + 7 * s, cy + 3 * s, cx - 7 * s, cy + 3 * s, p);
        c.DrawLine(cx - 7 * s, cy + 3 * s, cx - 4 * s, cy + 6 * s, p);
    }

    /// <summary>Two overlapping sheets: copy.</summary>
    public static void Copy(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.5f * s);
        c.DrawRoundRect(new SKRect(cx - 3 * s, cy - 3 * s, cx + 7 * s, cy + 7 * s), 2 * s, 2 * s, p);
        c.DrawLine(cx - 7 * s, cy + 3 * s, cx - 7 * s, cy - 5 * s, p);
        c.DrawLine(cx - 7 * s, cy - 7 * s, cx + 3 * s, cy - 7 * s, p);
    }

    /// <summary>A git branch: two dots joined, with a fork.</summary>
    public static void Branch(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawLine(cx - 4 * s, cy - 5 * s, cx - 4 * s, cy + 5 * s, p);
        using (var fork = new SKPathBuilder())
        {
            fork.MoveTo(cx + 5 * s, cy - 3 * s);
            fork.QuadTo(cx + 5 * s, cy + 2 * s, cx - 4 * s, cy + 3 * s);
            using var path = fork.Detach();
            c.DrawPath(path, p);
        }
        c.DrawCircle(cx - 4 * s, cy - 7 * s, 2 * s, p);
        c.DrawCircle(cx - 4 * s, cy + 7 * s, 2 * s, p);
        c.DrawCircle(cx + 5 * s, cy - 5 * s, 2 * s, p);
    }

    public static void Close(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.8f * s);
        c.DrawLine(cx - 5 * s, cy - 5 * s, cx + 5 * s, cy + 5 * s, p);
        c.DrawLine(cx + 5 * s, cy - 5 * s, cx - 5 * s, cy + 5 * s, p);
    }

    public static void Reset(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.7f * s);
        c.DrawArc(new SKRect(cx - 7 * s, cy - 7 * s, cx + 7 * s, cy + 7 * s), -60, 300, false, p);
        using var path = new SKPathBuilder();
        path.MoveTo(cx + 3.5f * s, cy - 9.5f * s);
        path.LineTo(cx + 7.5f * s, cy - 6.5f * s);
        path.LineTo(cx + 2.5f * s, cy - 4.5f * s);
        path.Close();
        Fill(c, path, color);
    }

    public static void File(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var stroke = Stroke(color, 1.5f * s);
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

    public static void Folder(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.5f * s);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 8 * s, cy - 6 * s);
        path.LineTo(cx - 3 * s, cy - 6 * s);
        path.LineTo(cx - 1 * s, cy - 4 * s);
        path.LineTo(cx + 8 * s, cy - 4 * s);
        path.LineTo(cx + 8 * s, cy + 7 * s);
        path.LineTo(cx - 8 * s, cy + 7 * s);
        path.Close();
        using var built = path.Detach();
        c.DrawPath(built, p);
    }

    public static void Chart(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.7f * s);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 8 * s, cy + 5 * s);
        path.LineTo(cx - 3 * s, cy - 1 * s);
        path.LineTo(cx + 1 * s, cy + 2 * s);
        path.LineTo(cx + 8 * s, cy - 6 * s);
        using var built = path.Detach();
        c.DrawPath(built, p);
    }

    /// <summary>A four-pointed spark, for AI agents.</summary>
    public static void Spark(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx, cy - 9 * s);
        path.CubicTo(cx + 1 * s, cy - 2 * s, cx + 2 * s, cy - 1 * s, cx + 9 * s, cy);
        path.CubicTo(cx + 2 * s, cy + 1 * s, cx + 1 * s, cy + 2 * s, cx, cy + 9 * s);
        path.CubicTo(cx - 1 * s, cy + 2 * s, cx - 2 * s, cy + 1 * s, cx - 9 * s, cy);
        path.CubicTo(cx - 2 * s, cy - 1 * s, cx - 1 * s, cy - 2 * s, cx, cy - 9 * s);
        path.Close();
        Fill(c, path, color);
    }

    public static void Gear(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawCircle(cx, cy, 3 * s, p);
        using var teeth = Stroke(color, 2.6f * s);
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4;
            c.DrawLine(cx + MathF.Cos(a) * 6 * s, cy + MathF.Sin(a) * 6 * s, cx + MathF.Cos(a) * 8.3f * s, cy + MathF.Sin(a) * 8.3f * s, teeth);
        }
        c.DrawCircle(cx, cy, 6.2f * s, p);
    }

    /// <summary>A monitor on a stand.</summary>
    public static void Screen(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var p = Stroke(color, size * 0.09f);
        var w = size * 0.42f;
        c.DrawRoundRect(new SKRect(cx - w, cy - size * 0.34f, cx + w, cy + size * 0.18f), size * 0.07f, size * 0.07f, p);
        c.DrawLine(cx, cy + size * 0.18f, cx, cy + size * 0.33f, p);
        c.DrawLine(cx - size * 0.2f, cy + size * 0.36f, cx + size * 0.2f, cy + size * 0.36f, p);
    }

    public static void Record(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        using var f = FillPaint(color);
        c.DrawCircle(cx, cy, size * 0.32f, f);
    }

    public static void Gamepad(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawRoundRect(new SKRect(cx - 9 * s, cy - 5 * s, cx + 9 * s, cy + 5.5f * s), 5 * s, 5 * s, p);
        c.DrawLine(cx - 5.5f * s, cy, cx - 2.5f * s, cy, p);
        c.DrawLine(cx - 4 * s, cy - 1.5f * s, cx - 4 * s, cy + 1.5f * s, p);
        using var f = FillPaint(color);
        c.DrawCircle(cx + 3.5f * s, cy - 0.5f * s, 1.2f * s, f);
        c.DrawCircle(cx + 5.8f * s, cy + 1.5f * s, 1.2f * s, f);
    }

    public static void Phone(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        c.DrawRoundRect(new SKRect(cx - 5.5f * s, cy - 9 * s, cx + 5.5f * s, cy + 9 * s), 2.5f * s, 2.5f * s, p);
        c.DrawLine(cx - 1.5f * s, cy + 6 * s, cx + 1.5f * s, cy + 6 * s, p);
    }

    public static void Bubble(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var p = Stroke(color, 1.6f * s);
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 8 * s, cy - 6 * s);
        path.LineTo(cx + 8 * s, cy - 6 * s);
        path.LineTo(cx + 8 * s, cy + 4 * s);
        path.LineTo(cx - 1 * s, cy + 4 * s);
        path.LineTo(cx - 5 * s, cy + 8 * s);
        path.LineTo(cx - 5 * s, cy + 4 * s);
        path.LineTo(cx - 8 * s, cy + 4 * s);
        path.Close();
        using var built = path.Detach();
        c.DrawPath(built, p);
    }

    public static void Pin(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var path = new SKPathBuilder();
        path.MoveTo(cx - 4 * s, cy - 8 * s);
        path.LineTo(cx + 4 * s, cy - 8 * s);
        path.LineTo(cx + 3 * s, cy - 2 * s);
        path.LineTo(cx + 6 * s, cy + 2 * s);
        path.LineTo(cx - 6 * s, cy + 2 * s);
        path.LineTo(cx - 3 * s, cy - 2 * s);
        path.Close();
        Fill(c, path, color);
        using var p = Stroke(color, 1.6f * s);
        c.DrawLine(cx, cy + 2 * s, cx, cy + 9 * s, p);
    }

    public static void Arrow(SKCanvas c, float cx, float cy, float size, SKColor color, bool up)
    {
        var s = size / 20f * (up ? -1 : 1);
        using var p = Stroke(color, 1.7f * Math.Abs(s));
        c.DrawLine(cx, cy - 6 * s, cx, cy + 6 * s, p);
        c.DrawLine(cx - 4 * s, cy + 2 * s, cx, cy + 6 * s, p);
        c.DrawLine(cx + 4 * s, cy + 2 * s, cx, cy + 6 * s, p);
    }

    // ───────────────────────────── weather ─────────────────────────────

    /// <summary>A WMO weather code as a small picture; <paramref name="colourful"/> tints it like the sky.</summary>
    public static void Weather(SKCanvas c, float cx, float cy, float size, int code, bool night, SKColor color, bool colourful)
    {
        var s = size / 20f;
        var sunColor = colourful ? new SKColor(0xFF, 0xC8, 0x3D) : color;
        var cloudColor = colourful ? new SKColor(0xE6, 0xEA, 0xF2) : color;
        var rainColor = colourful ? new SKColor(0x5A, 0xB4, 0xFF) : color;
        switch (code)
        {
            case 0:
                if (night) Moon(c, cx, cy, size, colourful ? new SKColor(0xF4, 0xE7, 0xA6) : color);
                else Sun(c, cx, cy, size, sunColor);
                return;
            case 1 or 2:
                if (night) Moon(c, cx - 3 * s, cy - 3 * s, size * 0.7f, colourful ? new SKColor(0xF4, 0xE7, 0xA6) : color);
                else Sun(c, cx - 3 * s, cy - 3 * s, size * 0.7f, sunColor);
                Cloud(c, cx + 1.5f * s, cy + 2.5f * s, size * 0.75f, cloudColor);
                return;
            case 45 or 48:
                Cloud(c, cx, cy - 2 * s, size * 0.8f, cloudColor);
                using (var p = Stroke(cloudColor.WithAlpha(180), 1.5f * s))
                {
                    c.DrawLine(cx - 8 * s, cy + 5 * s, cx + 8 * s, cy + 5 * s, p);
                    c.DrawLine(cx - 6 * s, cy + 8 * s, cx + 6 * s, cy + 8 * s, p);
                }
                return;
            case >= 51 and <= 67 or >= 80 and <= 82:
                Cloud(c, cx, cy - 2.5f * s, size * 0.85f, cloudColor);
                using (var p = Stroke(rainColor, 1.6f * s))
                    for (var i = -1; i <= 1; i++)
                        c.DrawLine(cx + i * 4 * s, cy + 4 * s, cx + i * 4 * s - 1.5f * s, cy + 8.5f * s, p);
                return;
            case >= 71 and <= 77 or 85 or 86:
                Cloud(c, cx, cy - 2.5f * s, size * 0.85f, cloudColor);
                using (var f = FillPaint(colourful ? SKColors.White : color))
                    for (var i = -1; i <= 1; i++)
                        c.DrawCircle(cx + i * 4 * s, cy + 6.5f * s + (i == 0 ? 1.5f * s : 0), 1.3f * s, f);
                return;
            case >= 95:
                Cloud(c, cx, cy - 2.5f * s, size * 0.85f, cloudColor);
                Bolt(c, cx, cy + 5.5f * s, size * 0.5f, colourful ? new SKColor(0xFF, 0xD2, 0x3F) : color);
                return;
            default:
                Cloud(c, cx, cy, size, cloudColor);
                return;
        }
    }

    public static void Cloud(SKCanvas c, float cx, float cy, float size, SKColor color)
    {
        var s = size / 20f;
        using var f = FillPaint(color);
        c.DrawCircle(cx - 4 * s, cy + 1 * s, 4.5f * s, f);
        c.DrawCircle(cx + 1 * s, cy - 2 * s, 6 * s, f);
        c.DrawCircle(cx + 5.5f * s, cy + 1.5f * s, 4 * s, f);
        c.DrawRoundRect(new SKRect(cx - 8.5f * s, cy + 1 * s, cx + 9.5f * s, cy + 5.5f * s), 2.2f * s, 2.2f * s, f);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static SKPaint FillPaint(SKColor color) => new() { Color = color, IsAntialias = true };

    private static SKPaint Stroke(SKColor color, float width) => new()
    {
        Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };

    private static void Fill(SKCanvas c, SKPathBuilder builder, SKColor color)
    {
        using var paint = FillPaint(color);
        using var path = builder.Detach();
        c.DrawPath(path, paint);
    }
}
