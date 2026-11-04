using HaloBar.Rendering;
using HaloBar.UI;
using SkiaSharp;
using Xunit;

namespace HaloBar.Tests;

/// <summary>
/// The Liquid Glass surface held to a product standard over every kind of screen behind it: solid
/// greys and colours, pastels, web pages, code, photos, stripes and noise, and splits of light and
/// dark. Each is rendered at six blurs, as the slim pill and the open island, then measured pixel
/// by pixel where text goes: the island's text must keep WCAG contrast (4.5:1, 3:1 for the dimmer
/// secondary text) for either text colour the glass might be showing. Then how it changes: no
/// steps, no waiting, no flicker. Set HALOBAR_GLASS_OUT to a folder to save every picture.
/// </summary>
public class GlassQualityTests
{
    private const int W = 820, H = 300;
    private static readonly SKColor Tint = new(0x10, 0x12, 0x18, 0x98);
    private static readonly int[] Blurs = [0, 15, 35, 50, 70, 100];

    public static IEnumerable<object[]> Cases() =>
        from name in Backgrounds.Keys
        from blur in Blurs
        from open in new[] { false, true }
        select new object[] { name, blur, open };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TextReadsOverEveryBackground(string background, int blur, bool open)
    {
        using var screen = Paint(Backgrounds[background]);
        var rect = open ? new SKRect(60, 6, W - 60, H - 24) : new SKRect(W / 2f - 170, 6, W / 2f + 170, 43);
        var radius = open ? 32f : rect.Height / 2;
        var area = SKRectI.Intersect(SKRectI.Round(SKRect.Inflate(rect, 24, 20)), new SKRectI(0, 0, W, H));
        using var full = SKBitmap.FromImage(screen);
        using var part = new SKBitmap();
        Assert.True(full.ExtractSubset(part, area));
        using var glass = GlassBackdrop.ShrinkAndBlur(part.GetPixels(), part.Width, part.Height, part.RowBytes, blur);
        var tone = GlassLight.Tone(glass);
        var frost = blur / 100f;

        // Either colour the text could be in here (the switch has a gap, so both can happen).
        var choices = new[] { LiquidGlass.WantsDarkText(tone, frost, false), LiquidGlass.WantsDarkText(tone, frost, true) }.Distinct();
        foreach (var dark in choices)
        {
            using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            c.DrawImage(screen, 0, 0);
            using var shape = new SKPath();
            shape.AddRoundRect(rect, radius, radius);
            LiquidGlass.Draw(c, shape, rect, radius, glass, area, open ? 1 : 0, dark ? 1 : 0, tone, frost, Tint);
            using var shot = surface.Snapshot();
            var ink = dark ? LiquidGlass.DarkInk : LiquidGlass.LightInk;
            // Where text goes: inside the bevel.
            var textArea = open ? SKRect.Inflate(rect, -30, -26) : SKRect.Inflate(rect, -22, -9);
            var (text, dim) = Legibility(shot, textArea, ink);
            Save(shot, $"{background}-blur{blur}-{(open ? "open" : "pill")}-{(dark ? "darktext" : "lighttext")}", ink, rect, open);
            Assert.True(text >= 4.5, $"{background} blur {blur} {(open ? "open" : "pill")}: text contrast {text:0.00} (dark text {dark}, tone {tone})");
            Assert.True(dim >= 3.0, $"{background} blur {blur} {(open ? "open" : "pill")}: secondary text contrast {dim:0.00} (dark text {dark}, tone {tone})");
        }
    }

    /// <summary>The text and dim-text contrast that 97% of the text area clears (glyphs are small; a few pixels may be worse).</summary>
    private static (double Text, double Dim) Legibility(SKImage shot, SKRect area, SKColor ink)
    {
        using var pixels = SKBitmap.FromImage(shot);
        var text = new List<double>();
        var dim = new List<double>();
        var inkL = Luminance(ink.Red, ink.Green, ink.Blue);
        var a = LiquidGlass.DimAlpha;
        for (var y = (int)area.Top; y < (int)area.Bottom; y += 2)
            for (var x = (int)area.Left; x < (int)area.Right; x += 2)
            {
                var p = pixels.GetPixel(x, y);
                var bg = Luminance(p.Red, p.Green, p.Blue);
                text.Add(Ratio(inkL, bg));
                var dl = Luminance(ink.Red * a + p.Red * (1 - a), ink.Green * a + p.Green * (1 - a), ink.Blue * a + p.Blue * (1 - a));
                dim.Add(Ratio(dl, bg));
            }
        text.Sort();
        dim.Sort();
        return (text[(int)(text.Count * 0.03)], dim[(int)(dim.Count * 0.03)]);
    }

    private static double Luminance(double r, double g, double b) =>
        0.2126 * LiquidGlass.Linear(r / 255) + 0.7152 * LiquidGlass.Linear(g / 255) + 0.0722 * LiquidGlass.Linear(b / 255);

    private static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    // ───────────────────────────── how it changes ─────────────────────────────

    [Fact]
    public void HazeNeverSteps()
    {
        // A screen brightening from black to white in tiny steps: the haze follows without a jump.
        foreach (var frost in new[] { 0f, 0.15f, 0.35f, 0.5f, 1f })
        foreach (var dark in new[] { 0f, 0.5f, 1f })
        foreach (var spread in new[] { 0.0, 0.1, 0.3 })
        {
            SKColor? last = null;
            for (var m = 0.0; m <= 1.0001; m += 0.002)
            {
                var tone = new GlassTone(m, Math.Max(0, m - spread), Math.Min(1, m + spread));
                var haze = LiquidGlass.Haze(tone, frost, dark, Tint);
                if (last is { } l)
                {
                    var step = Math.Max(Math.Max(Math.Abs(l.Red - haze.Red), Math.Abs(l.Green - haze.Green)),
                        Math.Max(Math.Abs(l.Blue - haze.Blue), Math.Abs(l.Alpha - haze.Alpha)));
                    Assert.True(step <= 3, $"frost {frost} dark {dark} spread {spread}: haze jumped {step} at {m:0.000}");
                }
                last = haze;
            }
        }
    }

    [Fact]
    public void TextSwitchesTheFrameTheBackgroundChanges()
    {
        // A dark window swapped for a white page: dark text from the very next decision.
        var dark = new GlassTone(0.1, 0.05, 0.15);
        var white = new GlassTone(0.95, 0.9, 1);
        Assert.False(LiquidGlass.WantsDarkText(dark, 0.5f, false));
        Assert.True(LiquidGlass.WantsDarkText(white, 0.5f, false));
        Assert.False(LiquidGlass.WantsDarkText(dark, 0.5f, true));
    }

    [Fact]
    public void TextNeverFlickersNearTheSwitch()
    {
        // The brightness wobbling a little around wherever the switch happens doesn't flip it back and forth.
        foreach (var frost in new[] { 0.35f, 0.5f, 1f })
        {
            var flip = 0.0;
            var was = false;
            for (var m = 0.0; m <= 1; m += 0.001)
            {
                var now = LiquidGlass.WantsDarkText(Tone(m), frost, was);
                if (now != was) { flip = m; was = now; break; }
            }
            Assert.True(was, $"frost {frost}: never turned dark");
            var flips = 0;
            for (var i = 0; i < 400; i++)
            {
                var m = flip + 0.025 * Math.Sin(i * 0.7);
                var now = LiquidGlass.WantsDarkText(Tone(m), frost, was);
                if (now != was) flips++;
                was = now;
            }
            Assert.True(flips == 0, $"frost {frost}: flickered {flips} times around {flip:0.000}");
        }
    }

    [Fact]
    public void TheSwitchHappensWhereBothColoursStillRead()
    {
        // Right at the switch, the haze still gives the old text colour its contrast (nothing unreadable in between).
        foreach (var frost in new[] { 0.35f, 0.5f, 0.7f, 1f })
            for (var m = 0.3; m <= 0.8; m += 0.01)
            {
                var tone = Tone(m);
                foreach (var dark in new[] { false, true })
                {
                    var haze = LiquidGlass.Haze(tone, frost, dark ? 1 : 0, Tint);
                    var a = haze.Alpha / 255.0;
                    var h = (0.2126 * haze.Red + 0.7152 * haze.Green + 0.0722 * haze.Blue) / 255;
                    var worst = dark ? tone.Low : tone.High;
                    var face = worst * (1 - a) + h * a;
                    var ink = dark ? 0.08 : 0.97;
                    Assert.True(LiquidGlass.Contrast(ink, face) >= 4.5, $"frost {frost} at {m:0.00}, dark text {dark}: {LiquidGlass.Contrast(ink, face):0.00}");
                }
            }
    }

    [Fact]
    public void CrossfadeStartsAtOnceAndIsSmooth()
    {
        // Text gliding light to dark at 60 frames a second, as Theme.Tick does.
        var k = (float)(1 - Math.Exp(-1.0 / 60 * Theme.TextRate));
        var c = LiquidGlass.LightInk;
        var first = Theme.Glide(c, LiquidGlass.DarkInk, k);
        Assert.True(c.Red - first.Red >= 20, "the crossfade should start on the first frame");
        var frames = 0;
        while (Math.Abs(c.Red - LiquidGlass.DarkInk.Red) > 3 && frames < 600)
        {
            var next = Theme.Glide(c, LiquidGlass.DarkInk, k);
            Assert.True(c.Red - next.Red <= 50, $"frame {frames}: a step of {c.Red - next.Red} is a jump, not a fade");
            c = next;
            frames++;
        }
        Assert.InRange(frames / 60.0, 0.15, 0.45);
    }

    [Fact]
    public void ToneSettlesInAFewFrames()
    {
        var k = 1 - Math.Exp(-1.0 / 60 * Theme.ToneRate);
        var tone = new GlassTone(0.1, 0.05, 0.15);
        var target = new GlassTone(0.9, 0.85, 0.95);
        var frames = 0;
        while (Math.Abs(tone.Mean - target.Mean) > 0.01) { tone = tone.Toward(target, k); frames++; }
        Assert.InRange(frames, 4, 20);
    }

    [Fact]
    public void RegularGlassStaysClearOverPlainBackgrounds()
    {
        // Glass, not a slab: over an even background the haze stays thin unless the text truly needs more.
        foreach (var frost in new[] { 0.35f, 0.5f, 0.7f })
            for (var m = 0.0; m <= 1.0001; m += 0.05)
            {
                var tone = new GlassTone(m, Math.Max(0, m - 0.03), Math.Min(1, m + 0.03));
                var dark = LiquidGlass.WantsDarkText(tone, frost, m > 0.5);
                var haze = LiquidGlass.Haze(tone, frost, dark ? 1 : 0, Tint);
                Assert.True(haze.Alpha <= 0.62 * 255, $"frost {frost} over {m:0.00}: haze {haze.Alpha / 255.0:0.00} is too thick");
            }
    }

    [Theory]
    [InlineData(40, 12)]
    [InlineData(900, 600)]
    [InlineData(2000, 1200)]
    public void DrawsAtAnySize(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width + 40, height + 40));
        using var glass = SKImage.FromBitmap(new SKBitmap(new SKImageInfo(8, 8)));
        var rect = new SKRect(20, 20, 20 + width, 20 + height);
        using var shape = new SKPath();
        shape.AddRoundRect(rect, 10, 10);
        LiquidGlass.Draw(surface.Canvas, shape, rect, 10, glass, rect, 0.5f, 0.5f, Tone(0.5), 0.5f, Tint);
    }

    private static GlassTone Tone(double m) => new(m, Math.Max(0, m - 0.08), Math.Min(1, m + 0.08));

    // ───────────────────────────── backgrounds ─────────────────────────────

    private static SKImage Paint(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        draw(surface.Canvas);
        return surface.Snapshot();
    }

    private static readonly Dictionary<string, Action<SKCanvas>> Backgrounds = Build();

    private static Dictionary<string, Action<SKCanvas>> Build()
    {
        var all = new Dictionary<string, Action<SKCanvas>>();
        for (var v = 0; v <= 255; v += 17)
        {
            var g = (byte)v;
            all[$"grey{v:000}"] = c => c.Clear(new SKColor(g, g, g));
        }
        for (var h = 0; h < 360; h += 30)
        {
            var hue = h;
            all[$"hue{h:000}"] = c => c.Clear(SKColor.FromHsv(hue, 100, 100));
            all[$"pastel{h:000}"] = c => c.Clear(SKColor.FromHsv(hue, 30, 100));
            if (h % 60 == 0) all[$"deep{h:000}"] = c => c.Clear(SKColor.FromHsv(hue, 100, 42));
        }
        all["page-light"] = c => Page(c, SKColors.White, new SKColor(0x20, 0x21, 0x24), new SKColor(0x1A, 0x0D, 0xAB));
        all["page-dark"] = c => Page(c, new SKColor(0x20, 0x21, 0x24), new SKColor(0xE8, 0xEA, 0xED), new SKColor(0x8A, 0xB4, 0xF8));
        all["page-sepia"] = c => Page(c, new SKColor(0xF4, 0xEC, 0xD8), new SKColor(0x5B, 0x46, 0x36), new SKColor(0x8B, 0x45, 0x13));
        all["code"] = Code;
        all["checker"] = c => Checker(c, 6);
        all["checker-big"] = c => Checker(c, 24);
        all["stripes-v"] = c => Stripes(c, true, 3);
        all["stripes-h"] = c => Stripes(c, false, 4);
        all["noise"] = Noise;
        all["gradient-h"] = c => Gradient(c, new SKPoint(0, 0), new SKPoint(W, 0), [SKColors.Black, SKColors.White]);
        all["gradient-v-light-top"] = c => Gradient(c, new SKPoint(0, 0), new SKPoint(0, H), [SKColors.White, SKColors.Black]);
        all["gradient-v-dark-top"] = c => Gradient(c, new SKPoint(0, 0), new SKPoint(0, H), [SKColors.Black, SKColors.White]);
        all["rainbow"] = c => Gradient(c, new SKPoint(0, 0), new SKPoint(W, 0),
            [SKColors.Red, SKColors.Orange, SKColors.Yellow, SKColors.Lime, SKColors.Cyan, SKColors.Blue, SKColors.Magenta]);
        all["wallpaper"] = Wallpaper;
        all["sunset"] = c => Gradient(c, new SKPoint(0, 0), new SKPoint(0, H), [new SKColor(0xFF, 0xB3, 0x47), new SKColor(0xFF, 0x5E, 0x62), new SKColor(0x3A, 0x1C, 0x71)]);
        all["night-city"] = NightCity;
        all["split-left-right"] = c => { c.Clear(SKColors.Black); using var p = new SKPaint { Color = SKColors.White }; c.DrawRect(0, 0, W / 2f, H, p); };
        all["split-top-bottom"] = c => { c.Clear(SKColors.Black); using var p = new SKPaint { Color = SKColors.White }; c.DrawRect(0, 0, W, 30, p); };
        all["browser-orange-tabs"] = c =>
        {
            Page(c, new SKColor(0x20, 0x21, 0x24), new SKColor(0xE8, 0xEA, 0xED), new SKColor(0x8A, 0xB4, 0xF8));
            using var p = new SKPaint { Color = new SKColor(0xFF, 0xB5, 0x4D) };
            c.DrawRect(0, 0, W, 34, p);
        };
        all["video-thumbnail"] = c =>
        {
            c.Clear(new SKColor(0x0F, 0x0F, 0x0F));
            using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(300, 0), new SKPoint(520, 120), [SKColors.White, new SKColor(0x3E, 0xA6, 0xFF)], SKShaderTileMode.Clamp) };
            c.DrawRect(280, 0, 260, 140, p);
        };
        return all;
    }

    private static void Page(SKCanvas c, SKColor paper, SKColor ink, SKColor link)
    {
        c.Clear(paper);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14);
        using var big = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 24);
        using var p = new SKPaint { IsAntialias = true, Color = ink };
        c.DrawText("The quick brown fox jumps over the lazy dog — Search results", 30, 28, big, p);
        for (var i = 0; i < 12; i++)
        {
            p.Color = i % 3 == 0 ? link : ink;
            c.DrawText("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore", 30, 52 + i * 20, font, p);
        }
    }

    private static void Code(SKCanvas c)
    {
        c.Clear(new SKColor(0x1E, 0x1E, 0x1E));
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 14);
        using var p = new SKPaint { IsAntialias = true };
        SKColor[] colours = [new(0x56, 0x9C, 0xD6), new(0xCE, 0x91, 0x78), new(0x6A, 0x99, 0x55), new(0xDC, 0xDC, 0xAA), new(0xD4, 0xD4, 0xD4)];
        for (var i = 0; i < 15; i++)
        {
            p.Color = colours[i % colours.Length];
            c.DrawText(new string(' ', i % 4 * 4) + "var picture = glass.Behind(screen, blur, pace); // line " + i, 12, 16 + i * 19, font, p);
        }
    }

    private static void Checker(SKCanvas c, int size)
    {
        c.Clear(SKColors.White);
        using var p = new SKPaint { Color = SKColors.Black };
        for (var y = 0; y < H; y += size)
            for (var x = (y / size % 2) * size; x < W; x += size * 2)
                c.DrawRect(x, y, size, size, p);
    }

    private static void Stripes(SKCanvas c, bool vertical, int size)
    {
        c.Clear(SKColors.White);
        using var p = new SKPaint { Color = SKColors.Black };
        for (var i = 0; i < (vertical ? W : H); i += size * 2)
            if (vertical) c.DrawRect(i, 0, size, H, p); else c.DrawRect(0, i, W, size, p);
    }

    private static void Noise(SKCanvas c)
    {
        var r = new Random(7);
        using var p = new SKPaint();
        for (var y = 0; y < H; y += 4)
            for (var x = 0; x < W; x += 4)
            {
                p.Color = new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
                c.DrawRect(x, y, 4, 4, p);
            }
    }

    private static void Gradient(SKCanvas c, SKPoint from, SKPoint to, SKColor[] colours)
    {
        using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(from, to, colours, SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, W, H, p);
    }

    private static void Wallpaper(SKCanvas c)
    {
        Gradient(c, new SKPoint(0, 0), new SKPoint(W, H), [new SKColor(0x7F, 0xD3, 0xFF), new SKColor(0xB5, 0x8C, 0xFF), new SKColor(0xFF, 0x9E, 0xC7)]);
        var r = new Random(4);
        using var p = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 18; i++)
        {
            p.Color = SKColor.FromHsv(r.Next(360), 70, 100).WithAlpha(200);
            c.DrawCircle(r.Next(W), r.Next(H), 14 + r.Next(40), p);
        }
    }

    private static void NightCity(SKCanvas c)
    {
        c.Clear(new SKColor(0x05, 0x08, 0x18));
        var r = new Random(11);
        using var p = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 260; i++)
        {
            p.Color = SKColor.FromHsv(30 + r.Next(40), 40, 100);
            c.DrawCircle(r.Next(W), r.Next(H), 1 + r.Next(3), p);
        }
    }

    private static void Save(SKImage shot, string name, SKColor ink, SKRect rect, bool open)
    {
        var folder = Environment.GetEnvironmentVariable("HALOBAR_GLASS_OUT");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.DrawImage(shot, 0, 0);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13);
        using var regular = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        using var p = new SKPaint { Color = ink, IsAntialias = true };
        if (open)
        {
            c.DrawText("Thursday 5 November", rect.Left + 30, rect.Top + 42, bold, p);
            p.Color = ink.WithAlpha(0x99);
            c.DrawText("26° Clear night · Ahmedabad · secondary text", rect.Left + 30, rect.Top + 64, regular, p);
        }
        else
        {
            c.DrawText("Now playing", rect.Left + 22, rect.MidY + 5, bold, p);
            p.Color = ink.WithAlpha(0x99);
            c.DrawText("12:37 AM", rect.Right - 22, rect.MidY + 5, SKTextAlign.Right, regular, p);
        }
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(folder, name + ".png"), data.ToArray());
    }
}
