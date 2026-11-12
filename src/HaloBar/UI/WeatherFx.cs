using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// Live weather drawn faintly inside the island, under its content: rain streaks, drifting snow,
/// a flash in a thunderstorm, slow fog, a few stars on a clear night, and a warm tint around
/// sunrise and sunset. Everything moves on its own clock, so it needs no state between frames
/// beyond a fixed set of seeds.
/// </summary>
internal static class WeatherFx
{
    private enum Kind { None, Drizzle, Rain, HeavyRain, Snow, Thunder, Fog, Stars }

    // Positions and speeds for up to 60 particles, the same every run (a fixed seed).
    private static readonly (float X, float Y, float Speed, float Size)[] Seeds = MakeSeeds();

    private static (float, float, float, float)[] MakeSeeds()
    {
        var random = new Random(31);
        return [.. Enumerable.Range(0, 60).Select(_ => (random.NextSingle(), random.NextSingle(), 0.6f + random.NextSingle() * 0.8f, random.NextSingle()))];
    }

    private static Kind KindOf(WeatherSnapshot w) => w.Code switch
    {
        51 or 53 or 55 or 56 or 57 => Kind.Drizzle,
        61 or 63 or 66 or 80 or 81 => Kind.Rain,
        65 or 67 or 82 => Kind.HeavyRain,
        71 or 73 or 75 or 77 or 85 or 86 => Kind.Snow,
        95 or 96 or 99 => Kind.Thunder,
        45 or 48 => Kind.Fog,
        0 or 1 when !w.IsDay => Kind.Stars,
        _ => Kind.None,
    };

    /// <summary>Whether anything moves now (the island then redraws often enough to show it).</summary>
    public static bool Moving(WeatherSnapshot? w) => w != null && KindOf(w) != Kind.None;

    /// <summary>Draws the weather into <paramref name="r"/> (already clipped to the island's outline).</summary>
    public static void Draw(SKCanvas canvas, SKRect r, WeatherSnapshot? w, DateTime now, bool lightSurface)
    {
        if (w == null || r.Height < 16) return;
        var t = (float)(now.TimeOfDay.TotalSeconds % 3600);
        SunTint(canvas, r, w, now, lightSurface);
        // On a light surface the particles are dark, otherwise light; always faint.
        var ink = lightSurface ? new SKColor(40, 50, 70) : new SKColor(220, 232, 255);
        switch (KindOf(w))
        {
            case Kind.Drizzle: Rain(canvas, r, t, ink, 14, 0.5f, 40); break;
            case Kind.Rain: Rain(canvas, r, t, ink, 26, 0.8f, 55); break;
            case Kind.HeavyRain: Rain(canvas, r, t, ink, 44, 1f, 70); break;
            case Kind.Thunder:
                Rain(canvas, r, t, ink, 34, 0.9f, 65);
                Flash(canvas, r, t);
                break;
            case Kind.Snow: Snow(canvas, r, t, ink); break;
            case Kind.Fog: Fog(canvas, r, t, ink); break;
            case Kind.Stars: Stars(canvas, r, t); break;
        }
    }

    private static void Rain(SKCanvas canvas, SKRect r, float t, SKColor ink, int count, float strength, byte alpha)
    {
        using var paint = new SKPaint { IsAntialias = true, StrokeWidth = 1.1f, StrokeCap = SKStrokeCap.Round };
        var fall = r.Height + 14;
        for (var i = 0; i < count; i++)
        {
            var (sx, sy, speed, size) = Seeds[i];
            var y = r.Top - 10 + (sy * fall + t * 120 * speed) % fall;
            var x = r.Left + sx * (r.Width + 20) - 10 - (y - r.Top) * 0.18f;
            var length = (5 + size * 6) * strength;
            paint.Color = ink.WithAlpha((byte)(alpha * (0.5f + size * 0.5f)));
            canvas.DrawLine(x, y, x - length * 0.18f, y + length, paint);
        }
    }

    private static void Flash(SKCanvas canvas, SKRect r, float t)
    {
        // About one flicker every 9 seconds, a quarter of a second long.
        var phase = t % 9f;
        if (phase > 0.25f) return;
        var a = (byte)(70 * (1 - phase / 0.25f));
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, a) };
        canvas.DrawRect(r, paint);
    }

    private static void Snow(SKCanvas canvas, SKRect r, float t, SKColor ink)
    {
        using var paint = new SKPaint { IsAntialias = true };
        var fall = r.Height + 8;
        for (var i = 0; i < 30; i++)
        {
            var (sx, sy, speed, size) = Seeds[i];
            var y = r.Top - 4 + (sy * fall + t * 14 * speed) % fall;
            var x = r.Left + sx * r.Width + MathF.Sin(t * 0.8f * speed + i) * 6;
            paint.Color = ink.WithAlpha((byte)(90 + size * 80));
            canvas.DrawCircle(x, y, 0.9f + size * 1.4f, paint);
        }
    }

    private static void Fog(SKCanvas canvas, SKRect r, float t, SKColor ink)
    {
        using var paint = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r.Height * 0.35f) };
        for (var i = 0; i < 3; i++)
        {
            var x = r.Left + ((t * (6 + i * 3) + i * r.Width / 3) % (r.Width * 1.4f)) - r.Width * 0.2f;
            paint.Color = ink.WithAlpha(34);
            canvas.DrawOval(new SKRect(x - r.Width * 0.25f, r.MidY - r.Height * 0.2f, x + r.Width * 0.25f, r.MidY + r.Height * 0.5f), paint);
        }
    }

    private static void Stars(SKCanvas canvas, SKRect r, float t)
    {
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 12; i++)
        {
            var (sx, sy, speed, size) = Seeds[i];
            var twinkle = 0.5f + 0.5f * MathF.Sin(t * (1.2f + speed) + i * 1.7f);
            paint.Color = new SKColor(255, 250, 230, (byte)(40 + 110 * twinkle * (0.4f + size * 0.6f)));
            canvas.DrawCircle(r.Left + 8 + sx * (r.Width - 16), r.Top + 4 + sy * (r.Height - 8), 0.6f + size * 0.8f, paint);
        }
    }

    /// <summary>
    /// Warm light near sunrise or sunset: a soft glow rising from the horizon edge, <em>added</em>
    /// over what's behind (like real light, with Plus) rather than painted on top. So dark evening
    /// glass warms and glows instead of smearing to a flat brown, and a bright surface only takes on
    /// a little warmth. Sunset leans amber into rose; sunrise a gentler gold. It eases in and out
    /// over about three quarters of an hour, so it never pops on or off.
    /// </summary>
    private static void SunTint(SKCanvas canvas, SKRect r, WeatherSnapshot w, DateTime now, bool lightSurface)
    {
        double best = 999;
        var rising = false;
        foreach (var (d, isRise) in new[] { (w.Sunrise, true), (w.Sunset, false) })
            if (d is { } v)
            {
                var m = Math.Abs((now - v).TotalMinutes);
                if (m < best) (best, rising) = (m, isRise);
            }
        if (best > 45) return;
        var k = (float)(1 - best / 45);
        k *= k * (3 - 2 * k);          // smoothstep: eases on and off, no hard edge
        if (lightSurface) k *= 0.45f;  // on bright glass the glow is barely needed and must not wash text

        byte A(float f) => (byte)Math.Clamp(f * 255f + 0.5f, 0, 255);
        var core = rising ? new SKColor(255, 196, 128) : new SKColor(255, 150, 82);   // bright at the horizon
        var sky = rising ? new SKColor(255, 178, 150) : new SKColor(238, 110, 128);   // the lit air above it
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus, // light, not paint: it adds, so it glows and never muddies
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(r.MidX, r.Bottom + r.Height * 0.12f), new SKPoint(r.MidX, r.Top - r.Height * 0.1f),
                [core.WithAlpha(A(0.24f * k)), sky.WithAlpha(A(0.10f * k)), SKColors.Transparent],
                [0f, 0.5f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(r, paint);
    }
}
