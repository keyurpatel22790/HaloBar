using HaloBar.Rendering;
using HaloBar.Services;
using HaloBar.UI;
using SkiaSharp;
using Xunit;

namespace HaloBar.Tests;

/// <summary>
/// The Liquid Glass renderer over made-up screens (a light web page, a dark code editor, a bright
/// wallpaper) at several blurs. Set HALOBAR_GLASS_OUT to a folder to save the pictures to look at.
/// </summary>
public class GlassRenderTests
{
    private const int W = 820, H = 300;

    [Fact]
    public void LensShaderBuilds() => Assert.True(LiquidGlass.Ready);

    [Fact]
    public void DrawsOverEveryBackgroundAndBlur()
    {
        var output = Environment.GetEnvironmentVariable("HALOBAR_GLASS_OUT");
        foreach (var (name, background) in new (string, Func<SKImage>)[] { ("light", Light), ("dark", Dark), ("wallpaper", Wallpaper) })
        {
            using var screen = background();
            foreach (var blur in new[] { 0, 35, 70 })
            foreach (var open in new[] { false, true })
            {
                using var shot = Render(screen, blur, open);
                using var pixels = SKBitmap.FromImage(shot);
                Assert.NotEqual(pixels.GetPixel(W / 2, 20), SKColors.Transparent);
                if (output == null) continue;
                Directory.CreateDirectory(output);
                using var data = shot.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, $"{name}-blur{blur}-{(open ? "open" : "pill")}.png"), data.ToArray());
            }
        }
    }

    [Fact]
    public void ClassicLookDrawsOverEveryBackground()
    {
        // The classic smoked glass: always dark, light text; saved for a look when HALOBAR_GLASS_OUT is set.
        var output = Environment.GetEnvironmentVariable("HALOBAR_GLASS_OUT");
        foreach (var (name, background) in new (string, Func<SKImage>)[] { ("light", Light), ("dark", Dark), ("wallpaper", Wallpaper) })
        foreach (var open in new[] { false, true })
        {
            using var screen = background();
            var rect = open ? new SKRect(60, 6, W - 60, H - 24) : new SKRect(W / 2f - 170, 6, W / 2f + 170, 42);
            var radius = open ? 32f : rect.Height / 2;
            var area = SKRectI.Intersect(SKRectI.Round(SKRect.Inflate(rect, 24, 20)), new SKRectI(0, 0, W, H));
            using var full = SKBitmap.FromImage(screen);
            using var part = new SKBitmap();
            Assert.True(full.ExtractSubset(part, area));
            using var glass = GlassBackdrop.ShrinkAndBlur(part.GetPixels(), part.Width, part.Height, part.RowBytes, 62);
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            surface.Canvas.DrawImage(screen, 0, 0);
            using var shape = new SKPath();
            shape.AddRoundRect(rect, radius, radius);
            LiquidGlass.DrawClassic(surface.Canvas, shape, rect, radius, glass, area, GlassLight.Tone(glass), 0.62f);
            using var paint = new SKPaint { Color = LiquidGlass.LightInk, IsAntialias = true };
            using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13);
            surface.Canvas.DrawText(open ? "Thursday 5 November" : "Now playing", rect.Left + 26, open ? rect.Top + 40 : rect.MidY + 5, bold, paint);
            using var shot = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(shot);
            // Smoked glass: the middle of the face is darker than a white page behind it.
            if (name == "light") Assert.True(pixels.GetPixel((int)rect.MidX, (int)rect.MidY).Red < 200);
            if (output == null) continue;
            Directory.CreateDirectory(output);
            using var data = shot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, $"classic-{name}-{(open ? "open" : "pill")}.png"), data.ToArray());
        }
    }

    [Fact]
    public void WhatsInTheHoleNeverSwaysTheMeasuredBrightness()
    {
        // The glass patches its own spot while recording; that patch, however dark or bright, must
        // not move the brightness the glass reads, or the island blinks once a frame.
        static SKImage Picture(SKColor around, SKColor patch)
        {
            using var surface = SKSurface.Create(new SKImageInfo(240, 160, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(around);
            using var p = new SKPaint { Color = patch };
            surface.Canvas.DrawRect(new SKRect(80, 50, 160, 110), p); // the "hole"
            return surface.Snapshot();
        }
        var hole = new SKRectI(80, 50, 160, 110);
        var grey = new SKColor(0x88, 0x88, 0x88);
        using var black = Picture(grey, SKColors.Black);
        using var white = Picture(grey, SKColors.White);
        using var same = Picture(grey, grey);
        var tBlack = GlassLight.Tone(black, hole);
        var tWhite = GlassLight.Tone(white, hole);
        var tSame = GlassLight.Tone(same, hole);
        Assert.True(Math.Abs(tBlack.Mean - tWhite.Mean) < 0.01, $"patch swayed the mean: {tBlack.Mean:0.000} vs {tWhite.Mean:0.000}");
        Assert.True(Math.Abs(tBlack.Mean - tSame.Mean) < 0.01 && Math.Abs(tBlack.High - tSame.High) < 0.02 && Math.Abs(tBlack.Low - tSame.Low) < 0.02,
            $"excluding the hole should read the surround: {tBlack} vs {tSame}");
        // Measured over an image whose top half is one shade, the mean should be that shade (~0.53 for 0x88).
        Assert.InRange(tBlack.Mean, 0.45, 0.62);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NothingTheGlassDrawsReachesPastItsReach(bool open)
    {
        // While recording, the island's spot is cut out of the screen copy as far as Reach says; if
        // its shadow went further, the glass would read its own shadow and blink.
        using var white = Draw(c => c.Clear(SKColors.White));
        var rect = open ? new SKRect(150, 10, W - 150, 250) : new SKRect(W / 2f - 170, 10, W / 2f + 170, 47);
        var lift = Math.Clamp((rect.Height - 34) / 120f, 0, 1);
        var area = new SKRectI(0, 0, W, H);
        using var full = SKBitmap.FromImage(white);
        using var glass = GlassBackdrop.ShrinkAndBlur(full.GetPixels(), full.Width, full.Height, full.RowBytes, 50);
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(SKColors.White);
        using var shape = new SKPath();
        shape.AddRoundRect(rect, 20, 20);
        LiquidGlass.Draw(surface.Canvas, shape, rect, 20, glass, area, lift, 1, GlassLight.Tone(glass), 0.5f, new SKColor(0x10, 0x12, 0x18, 0x98));
        using var shot = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(shot);
        var (sides, below) = LiquidGlass.Reach(lift);
        var reach = new SKRect(rect.Left - sides, rect.Top - sides, rect.Right + sides, rect.Bottom + below);
        for (var y = 0; y < H; y += 2)
            for (var x = 0; x < W; x += 2)
            {
                if (reach.Contains(x, y)) continue;
                var p = pixels.GetPixel(x, y);
                Assert.True(p.Red >= 254 && p.Green >= 254 && p.Blue >= 254, $"drawn at {x},{y} ({p}), outside the reach {reach}");
            }
    }

    [Fact]
    public void RecordingFillsTheIslandsSpotFromAroundIt()
    {
        // An orange screen with the (black) island itself in the copy, as while recording.
        using var screen = Draw(c =>
        {
            c.Clear(new SKColor(0xFF, 0x90, 0x30));
            using var island = new SKPaint { Color = SKColors.Black };
            c.DrawRect(300, 0, 520, 60, island);
        });
        using var full = SKBitmap.FromImage(screen);
        using var glass = GlassBackdrop.ShrinkAndBlur(full.GetPixels(), full.Width, full.Height, full.RowBytes, 35, new SKRectI(300, 0, 520, 60));
        using var pixels = SKBitmap.FromImage(glass);
        var shrink = GlassBackdrop.ShrinkFor(35);
        var middle = pixels.GetPixel(410 / shrink, 25 / shrink);
        Assert.True(middle.Red > 200 && middle.Green > 100 && middle.Blue < 110, $"filled with {middle}");
    }

    [Fact]
    public void SunsetLightGlowsWithoutMuddying()
    {
        // The evening weather glow (WeatherFx.SunTint) over dark glass: it must warm and glow, not
        // smear a flat brown. Saved for a look when HALOBAR_GLASS_OUT is set.
        var output = Environment.GetEnvironmentVariable("HALOBAR_GLASS_OUT");
        var now = new DateTime(2026, 11, 7, 18, 0, 0);
        var sunset = new WeatherSnapshot("Ahmedabad", 29, 0, IsDay: true, 34, 23, Fahrenheit: false, now) { Sunset = now };
        foreach (var (name, background) in new (string, Func<SKImage>)[] { ("dark", Dark), ("wallpaper", Wallpaper) })
        foreach (var open in new[] { false, true })
        {
            using var screen = background();
            var rect = open ? new SKRect(60, 6, W - 60, H - 24) : new SKRect(W / 2f - 170, 6, W / 2f + 170, 42);
            var radius = open ? 32f : rect.Height / 2;
            var area = SKRectI.Intersect(SKRectI.Round(SKRect.Inflate(rect, 24, 20)), new SKRectI(0, 0, W, H));
            using var full = SKBitmap.FromImage(screen);
            using var part = new SKBitmap();
            Assert.True(full.ExtractSubset(part, area));
            using var glass = GlassBackdrop.ShrinkAndBlur(part.GetPixels(), part.Width, part.Height, part.RowBytes, 45);
            var tone = GlassLight.Tone(glass);
            var lightSurface = name == "wallpaper";
            var dark = lightSurface ? 1f : 0f;

            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            var c = surface.Canvas;
            c.DrawImage(screen, 0, 0);
            using var shape = new SKPath();
            shape.AddRoundRect(rect, radius, radius);
            LiquidGlass.Draw(c, shape, rect, radius, glass, area, open ? 1 : 0, dark, tone, 0.45f, new SKColor(0x10, 0x10, 0x14, 200));
            // The weather is drawn clipped to the island, under its content, exactly as IslandView does.
            c.Save();
            c.ClipPath(shape, antialias: true);
            WeatherFx.Draw(c, rect, sunset, now, lightSurface);
            c.Restore();
            var ink = lightSurface ? new SKColor(0x14, 0x14, 0x18) : new SKColor(0xF7, 0xF7, 0xFA);
            using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13);
            using var paint = new SKPaint { Color = ink, IsAntialias = true };
            c.DrawText(open ? "Saturday 7 November · sunset" : "6:00 PM · sunset", rect.Left + 24, open ? rect.Top + 40 : rect.MidY + 5, bold, paint);

            using var shot = surface.Snapshot();
            if (output == null) continue;
            Directory.CreateDirectory(output);
            using var data = shot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, $"sunset-{name}-{(open ? "open" : "pill")}.png"), data.ToArray());
        }
    }

    private static SKImage Render(SKImage screen, int blur, bool open)
    {
        var rect = open ? new SKRect(60, 6, W - 60, H - 24) : new SKRect(W / 2f - 170, 6, W / 2f + 170, 42);
        var radius = open ? 32f : rect.Height / 2;
        var area = SKRectI.Intersect(SKRectI.Round(SKRect.Inflate(rect, 24, 20)), new SKRectI(0, 0, W, H));
        using var full = SKBitmap.FromImage(screen);
        using var part = new SKBitmap();
        Assert.True(full.ExtractSubset(part, area));
        using var glass = GlassBackdrop.ShrinkAndBlur(part.GetPixels(), part.Width, part.Height, part.RowBytes, blur);
        var tone = GlassLight.Tone(glass);
        var light = (float)tone.Mean;
        var frost = blur / 100f;
        var dark = LiquidGlass.WantsDarkText(tone, frost, false) ? 1f : 0f;

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.DrawImage(screen, 0, 0);
        using var shape = new SKPath();
        shape.AddRoundRect(rect, radius, radius);
        LiquidGlass.Draw(c, shape, rect, radius, glass, area, open ? 1 : 0, dark, tone, frost, new SKColor(0x10, 0x10, 0x14, 200));

        var ink = dark > 0.5f ? new SKColor(0x14, 0x14, 0x18) : new SKColor(0xF7, 0xF7, 0xFA);
        var dim = ink.WithAlpha(170);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13);
        using var regular = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        if (!open)
        {
            c.DrawText("Now playing", rect.Left + 22, rect.MidY + 5, bold, paint);
            c.DrawText("12:37 AM", rect.Right - 22, rect.MidY + 5, SKTextAlign.Right, bold, paint);
        }
        else
        {
            c.DrawText("Thursday 5 November", rect.Left + 30, rect.Top + 40, bold, paint);
            paint.Color = dim;
            c.DrawText("26° Clear night · Ahmedabad", rect.Left + 30, rect.Top + 62, regular, paint);
            using var tile = new SKPaint { Color = ink.WithAlpha(22), IsAntialias = true };
            for (var i = 0; i < 3; i++)
            {
                var t = new SKRect(rect.Left + 30 + i * 230, rect.Top + 84, rect.Left + 240 + i * 230, rect.Bottom - 26);
                c.DrawRoundRect(t, 18, 18, tile);
                paint.Color = ink;
                c.DrawText(new[] { "CPU 27%", "Focus 25:00", "Music" }[i], t.Left + 16, t.Top + 30, bold, paint);
                paint.Color = dim;
                c.DrawText("A line of detail", t.Left + 16, t.Top + 52, regular, paint);
            }
        }
        return surface.Snapshot();
    }

    private static SKImage Draw(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        draw(surface.Canvas);
        return surface.Snapshot();
    }

    // A white web page: a header, a photo, and lines of text and links.
    private static SKImage Light() => Draw(c =>
    {
        c.Clear(SKColors.White);
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(0xF1, 0xF3, 0xF4) };
        c.DrawRect(0, 0, W, 30, p);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14);
        using var big = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 22);
        p.Color = new SKColor(0x20, 0x21, 0x24);
        c.DrawText("Search results  ·  Images  ·  News  ·  Videos  ·  Shopping", 40, 20, font, p);
        c.DrawText("The quick brown fox jumps over the lazy dog", 40, 66, big, p);
        for (var i = 0; i < 9; i++)
        {
            p.Color = i % 3 == 0 ? new SKColor(0x1A, 0x0D, 0xAB) : new SKColor(0x4D, 0x51, 0x56);
            c.DrawText("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt", 40, 96 + i * 21, font, p);
        }
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(600, 40), new SKPoint(780, 200), [new SKColor(0xFF, 0x9A, 0x3C), new SKColor(0xE0, 0x3E, 0x6B)], SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(610, 40, 790, 190), 12, 12, p);
    });

    // A dark code editor.
    private static SKImage Dark() => Draw(c =>
    {
        c.Clear(new SKColor(0x1E, 0x1E, 0x1E));
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 14);
        using var p = new SKPaint { IsAntialias = true };
        SKColor[] colours = [new(0x56, 0x9C, 0xD6), new(0xCE, 0x91, 0x78), new(0x6A, 0x99, 0x55), new(0xDC, 0xDC, 0xAA), new(0xD4, 0xD4, 0xD4)];
        for (var i = 0; i < 14; i++)
        {
            p.Color = colours[i % colours.Length];
            c.DrawText(new string(' ', i % 4 * 4) + "var picture = glass.Behind(screen, blur, pace); // line " + i, 20, 18 + i * 20, font, p);
        }
    });

    // A bright, colourful wallpaper.
    private static SKImage Wallpaper() => Draw(c =>
    {
        using var p = new SKPaint { IsAntialias = true };
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
            [new SKColor(0x7F, 0xD3, 0xFF), new SKColor(0xB5, 0x8C, 0xFF), new SKColor(0xFF, 0x9E, 0xC7)], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, W, H, p);
        p.Shader = null;
        var r = new Random(4);
        for (var i = 0; i < 18; i++)
        {
            p.Color = SKColor.FromHsv(r.Next(360), 70, 100).WithAlpha(200);
            c.DrawCircle(r.Next(W), r.Next(H), 14 + r.Next(40), p);
        }
    });
}
