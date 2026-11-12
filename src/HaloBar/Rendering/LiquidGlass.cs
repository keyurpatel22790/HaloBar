using HaloBar.UI;
using SkiaSharp;

namespace HaloBar.Rendering;

/// <summary>
/// The Glass surface, after Apple's Liquid Glass (iOS 26): a piece of glass with a flat face and a
/// rounded bevel, laid over the picture of the screen behind it.
/// <list type="bullet">
/// <item>The face shows what's behind as it is (blurred as much as the blur setting asks), a little
/// more saturated (that's done where the picture is made).</item>
/// <item>Only the bevel bends light, by Snell's law through a convex edge (glass's index, 1.5): at
/// the outline the picture comes from well inside, easing to none where the bevel meets the face.
/// Red and blue bend 2% differently for a faint fringe.</item>
/// <item>Two equal highlights on opposite corners (top right, bottom left) catch a thin line along
/// the outline over a soft glow, made of a saturated, brightened copy of what's behind rather than
/// flat white; the bevel facing away is a shade darker, more so over a bright background, where that
/// shading is what shows the glass's thickness. Near the rim the glass is less blurred.</item>
/// <item>It adapts at every blur: a white haze under dark text over a bright background, a dark one
/// under light text, each as thin as the look wants and as thick as the text needs.</item>
/// <item>Outside: a soft shadow, and a hairline darker contour that keeps the edge on white.</item>
/// </list>
/// Written for HaloBar from Apple's descriptions of the look and from what developers found
/// inspecting it on macOS and iOS 26 (Apple's glass is one private glassBackground filter driven by
/// a distance field, with two highlight layers and a shadow): github.com/AlexStrNik/ShatteredGlass,
/// sspai.com/post/100983, github.com/ktiays/GlassExplorer.
/// </summary>
internal static class LiquidGlass
{
    // The values follow what developers found inside Apple's glass on macOS and iOS 26 (its private
    // glassBackground filter and highlight layers; see the class summary): a 12.5-point inner
    // band that pulls the picture 40 points inward, a 6.25-point outer band at the rim that bends
    // it 10 back the other way, and two equal highlights on opposite corners made of a saturated,
    // brightened copy of what's behind (curvature 1.7, spread 1.59 radians). Scaled down a little
    // on small glass, as Apple's lensing is stronger on bigger pieces.
    private const string LensSksl = @"
        uniform shader behind;
        uniform shader sharp;    // the same, unblurred
        uniform float2 centre;
        uniform float2 extent;   // half the glass's width and height
        uniform float radius;
        uniform float ring;      // the inner band's width, in from the outline
        uniform float bend;      // how far it pulls the picture in, right at the outline
        uniform float rimRing;   // the outer band's width at the very rim
        uniform float rimBend;   // how far it bends the picture back out
        uniform float glow;      // highlight strength (Apple: 0.5)
        uniform float crispBand; // how far in from the rim the glass is less blurred (Apple: 25)
        uniform float spread;    // the highlights' angular spread (Apple: 1.59 regular, 2.75 clear)
        uniform float shade;     // how much the bevel facing away darkens
        uniform half4 haze;      // the face's haze (unpremultiplied), laid on before the light
        float sdf(float2 p) {
            float2 q = abs(p - centre) - (extent - radius);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }
        // Apple's vibrantColorMatrix for the highlights: strongly saturated, slightly lifted.
        half3 vibrant(half3 c) {
            return clamp(half3(2.6705 * c.r - 1.1088 * c.g - 0.1117 * c.b,
                               -0.3295 * c.r + 1.8914 * c.g - 0.1119 * c.b,
                               -0.3297 * c.r - 1.1084 * c.g + 2.8881 * c.b) + 0.05, 0.0, 1.0);
        }
        half4 main(float2 p) {
            float depth = max(-sdf(p), 0.0);
            float2 grad = float2(sdf(p + float2(0.75, 0.0)) - sdf(p - float2(0.75, 0.0)),
                                 sdf(p + float2(0.0, 0.75)) - sdf(p - float2(0.0, 0.75)));
            float2 n = grad / max(length(grad), 0.0001);
            // Inner band, Snell through a convex edge: none where it meets the face, the most at the outline.
            float x = clamp(1.0 - depth / ring, 0.0, 1.0);
            float ti = asin(x * x);
            float tt = asin(sin(ti) / 1.5);
            float pull = tan(ti - tt) / 1.118;
            // Outer band: right at the rim the picture bends back out a little.
            float r = clamp(1.0 - depth / rimRing, 0.0, 1.0);
            float2 o = -n * bend * pull + n * rimBend * r * r;
            half3 c;
            c.r = behind.eval(p + o * 0.98).r;
            c.g = behind.eval(p + o).g;
            c.b = behind.eval(p + o * 1.02).b;
            // Apple's blur stops: fully blurred from 25 points in (less on small glass, so its text
            // area stays fully blurred), half blurred from 1 point in to the rim: a crisper edge.
            half3 crisp;
            crisp.r = sharp.eval(p + o * 0.98).r;
            crisp.g = sharp.eval(p + o).g;
            crisp.b = sharp.eval(p + o * 1.02).b;
            float blurred = mix(0.5, 1.0, smoothstep(1.0, crispBand, depth));
            c = mix(crisp, c, half(blurred));
            c = mix(c, haze.rgb, haze.a);
            // Two highlights at opposite angles (top right, bottom left), equally strong, falling off
            // with the angle (spread 1.59, curvature 1.7) and with depth: a crisp line on the rim
            // over a soft glow (Apple's smoothness 8).
            float a = atan(n.y, n.x);
            // Apple's angles, pi/4 and -3pi/4 with y up (macOS layers): top right and bottom left
            // here, where y points down.
            float d1 = abs(mod(a + 0.78540 + 3.14159, 6.28318) - 3.14159);
            float d2 = abs(mod(a - 2.35619 + 3.14159, 6.28318) - 3.14159);
            float lit = pow(max(cos(min(d1, d2) * 1.5708 / spread), 0.0), 1.7);
            float line = 1.0 - smoothstep(0.3, 1.5, depth);
            float soft = exp(-depth / 2.8) * 0.45;
            float s = clamp(glow * lit * (line + soft) + 0.18 * line, 0.0, 1.0);
            half3 light = mix(vibrant(c), half3(1.0), 0.45);
            c = 1.0 - (1.0 - c) * (1.0 - light * half(s)); // screen: it brightens, never greys
            // The bevel's shading: darker facing away from the light and toward the outline.
            float away = 0.35 + 0.65 * max(-dot(n, normalize(float2(1.0, -1.0))), 0.0);
            c *= half(1.0 - shade * pull * away);
            return half4(c, 1.0);
        }";

    private static readonly SKRuntimeEffect? Lens = Build(LensSksl);

    // The classic look, measured from the glass HaloBar set out to match (NotchPixel): a thick pane
    // whose rim curves over 2.4 times its bend (13 points), cubically, so the middle is flat and the
    // edge magnifies; a 16% colour fringe; a smoked tint (the surface's colour) a shade thicker
    // toward the rim; a thin rim of light from the top left, never below a third of its strength;
    // and a faint glow along the top 14 points. Written for HaloBar from those measurements.
    private const string ClassicSksl = @"
        uniform shader behind;
        uniform float2 centre;
        uniform float2 extent;
        uniform float radius;
        uniform float bend;      // how far the rim pulls the picture in, at the outline
        uniform half4 tint;      // the smoke: colour, and how thick it is in the middle
        uniform float rimk;      // the rim light's strength (less on a thin strip, nearly all rim)
        uniform float top;       // the glass's top edge
        float sdf(float2 p) {
            float2 q = abs(p - centre) - (extent - radius);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }
        half4 main(float2 p) {
            float d = sdf(p);
            float2 g = float2(sdf(p + float2(1.0, 0.0)) - sdf(p - float2(1.0, 0.0)),
                              sdf(p + float2(0.0, 1.0)) - sdf(p - float2(0.0, 1.0)));
            g = length(g) > 0.0001 ? normalize(g) : float2(0.0, -1.0);
            float t = clamp(1.0 + d / (bend * 2.4), 0.0, 1.0);
            float curve = t * t * t;
            float2 o = -g * bend * curve;
            half3 c;
            c.r = behind.eval(p + o * 1.16).r;
            c.g = behind.eval(p + o).g;
            c.b = behind.eval(p + o * 0.84).b;
            c = mix(c, tint.rgb, half(clamp(tint.a + 0.12 * curve, 0.0, 0.95)));
            float rim = smoothstep(-2.2, -0.2, d) * (1.0 - smoothstep(-0.2, 0.6, d));
            float facing = 0.35 + 0.65 * clamp(dot(g, float2(-0.55, -0.85)), 0.0, 1.0);
            c += half3(rim * facing * 0.55 * rimk);
            float glow = clamp(1.0 - (p.y - top) / 14.0, 0.0, 1.0);
            c += half3(0.09 * glow * glow * rimk);
            return half4(c, 1.0);
        }";

    private static readonly SKRuntimeEffect? Classic = Build(ClassicSksl);

    /// <summary>The lens shader built (false: it didn't compile, and the glass is drawn flat).</summary>
    public static bool Ready => Lens != null && Classic != null;

    private static SKRuntimeEffect? Build(string sksl)
    {
        var effect = SKRuntimeEffect.CreateShader(sksl, out var errors);
        if (effect == null) Log.Write($"Glass: lens shader didn't build: {errors}");
        return effect;
    }

    private static float ShadowBlur(float lift) => 9 + 11 * lift;
    private static float ShadowDrop(float lift) => 4 + 4 * lift;

    /// <summary>
    /// How far outside the glass anything it draws reaches (points): the shadow's blur (three
    /// sigmas) and drop below, the blur alone to the sides and above. Classic draws nothing outside.
    /// While the island shows in captures, its spot in the screen copy is cut out this far, or the
    /// glass would read its own shadow, measure the screen darker, lighten the shadow, and so on:
    /// a blink.
    /// </summary>
    public static (float Sides, float Below) Reach(float lift) =>
        Prefs.GlassClassic ? (2, 2) : (3 * ShadowBlur(lift) + 2, 3 * ShadowBlur(lift) + ShadowDrop(lift) + 2);

    /// <summary>Below this blur the glass is clear: the picture is kept sharp (full size) and dimmed with black.</summary>
    public static bool IsClear(float frost) => frost < 0.2f;

    // ───────────────────────────── legibility ─────────────────────────────

    /// <summary>The text colours on glass: light over dark backgrounds, dark over light ones.</summary>
    public static readonly SKColor LightInk = new(0xF7, 0xF7, 0xFA), DarkInk = new(0x14, 0x14, 0x18);

    /// <summary>
    /// Contrast the glass keeps for its text, with a margin over the minimums (WCAG's 4.5:1 for
    /// text, 3:1 for the dimmer secondary text, drawn at 60%).
    /// </summary>
    internal const double TextContrast = 5.4, DimContrast = 3.9, DimAlpha = 0x99 / 255.0;

    /// <summary>The brightest background (sRGB luma) light text still reads on, and the darkest dark text does.</summary>
    internal static readonly double LightLimit = Limit(Ink(LightInk), lightText: true), DarkLimit = Limit(Ink(DarkInk), lightText: false);

    private static double Ink(SKColor c) =>
        Grey(0.2126 * Linear(c.Red / 255.0) + 0.7152 * Linear(c.Green / 255.0) + 0.0722 * Linear(c.Blue / 255.0));

    /// <summary>sRGB value to linear light.</summary>
    internal static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>Linear light to the sRGB grey value with it.</summary>
    internal static double Grey(double linear) => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

    /// <summary>WCAG contrast ratio of two sRGB greys.</summary>
    internal static double Contrast(double a, double b)
    {
        double la = Linear(a), lb = Linear(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static bool Reads(double ink, double v) =>
        Contrast(ink, v) >= TextContrast && Contrast(ink * DimAlpha + v * (1 - DimAlpha), v) >= DimContrast;

    private static double Limit(double ink, bool lightText)
    {
        // Light text reads on everything darker than the limit; dark text on everything brighter.
        double lo = 0, hi = 1;
        for (var i = 0; i < 40; i++)
        {
            var mid = (lo + hi) / 2;
            if (Reads(ink, mid) == lightText) lo = mid; else hi = mid;
        }
        return lightText ? lo : hi;
    }

    /// <summary>
    /// How thick (0–1) the haze under light text must be, at least <paramref name="floor"/>: thick
    /// enough to bring the brightest part behind down to where light text reads.
    /// </summary>
    internal static double LightHaze(GlassTone tone, double hazeLuma, double floor)
    {
        var top = tone.High;
        var need = top <= LightLimit ? 0 : (top - LightLimit) / Math.Max(0.05, top - hazeLuma);
        return Math.Clamp(Math.Max(floor, need), 0, 0.94);
    }

    /// <summary>How thick the white haze under dark text must be: the darkest part behind up to where dark text reads.</summary>
    internal static double DarkHaze(GlassTone tone, double floor)
    {
        var bottom = tone.Low;
        var need = bottom >= DarkLimit ? 0 : (DarkLimit - bottom) / Math.Max(0.05, 1 - bottom);
        return Math.Clamp(Math.Max(floor, need), 0, 0.94);
    }

    /// <summary>The dark haze's colour: the surface's own, unless that's too light to dim with.</summary>
    internal static SKColor DimColour(SKColor tint) => Ink(tint) > 0.2 ? new SKColor(0x10, 0x10, 0x14) : tint.WithAlpha(255);

    // How thick the haze is at least, for the look: thinner the frostier the glass.
    private static double LightFloor(float frost) => 0.2 + 0.12 * (1 - frost);
    private static double DarkFloor(float frost) => 0.24 + 0.14 * (1 - frost);

    /// <summary>
    /// Dark text or light for what's behind: whichever needs the thinner haze (so the glass stays
    /// as clear as it can), switching only once the other is clearly better, so it never flickers.
    /// </summary>
    public static bool WantsDarkText(GlassTone tone, float frost, bool wasDark)
    {
        if (Prefs.GlassClassic) return false; // smoked glass: light text, always
        var light = LightHaze(tone, 0.07, 0);
        var dark = DarkHaze(tone, 0);
        // Over a plain mid grey both need some; lean on the brightness itself.
        var bias = (tone.Mean - 0.55) * 0.5;
        var lean = light - dark + bias; // above 0: dark text is the better fit
        return wasDark ? lean > -0.08 : lean > 0.08;
    }

    /// <summary>The haze over the glass's face for <paramref name="tone"/>, between the light-text and dark-text kinds by <paramref name="darkText"/>.</summary>
    public static SKColor Haze(GlassTone tone, float frost, float darkText, SKColor tint)
    {
        var dim = DimColour(tint);
        var under = IsClear(frost) ? SKColors.Black : dim;
        // Clear glass keeps no haze floor: it dims only as much as the text behind actually forces
        // (the LightHaze/DarkHaze "need"), so over a readable backdrop it stays truly see-through.
        var lightAlpha = LightHaze(tone, Ink(under), IsClear(frost) ? 0.0 : LightFloor(frost));
        var forLight = under.WithAlpha((byte)Math.Round(255 * lightAlpha));
        if (darkText <= 0.001f) return forLight;
        var forDark = SKColors.White.WithAlpha((byte)Math.Round(255 * DarkHaze(tone, IsClear(frost) ? 0.0 : DarkFloor(frost))));
        return Theme.Glide(forLight, forDark, darkText);
    }

    /// <param name="glass">The picture of the screen behind, covering <paramref name="area"/>.</param>
    /// <param name="lift">0 for the slim pill, 1 for the open island (a deeper shadow).</param>
    /// <param name="darkText">0–1: how far the text has turned dark (follows its glide, so nothing jumps).</param>
    /// <param name="tone">How bright the screen behind is, and its darkest and brightest parts.</param>
    /// <param name="frost">0–1: the blur setting.</param>
    /// <param name="tint">The surface's own colour, for the haze under light text.</param>
    public static void Draw(SKCanvas canvas, SKPath shape, SKRect rect, float radius, SKImage glass, SKRect area,
        float lift, float darkText, GlassTone tone, float frost, SKColor tint)
    {
        if (Prefs.GlassClassic && Classic != null)
        {
            DrawClassic(canvas, shape, rect, radius, glass, area, tone, frost);
            return;
        }
        var clear = IsClear(frost);
        var bright = (float)Math.Clamp((tone.Mean - 0.35) / 0.45, 0, 1);

        // Outside only: inside, a shadow would darken what shows through. Apple's is wide and soft
        // (offset 8, blur radius 40, darkening what's under it by about a fifth); the slim pill
        // gets a smaller one.
        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Difference, true);
        using (var shadow = new SKPaint
               {
                   Color = SKColors.Black.WithAlpha((byte)(34 + 14 * bright + 22 * lift)), IsAntialias = true,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ShadowBlur(lift)),
               })
        {
            canvas.Save();
            canvas.Translate(0, ShadowDrop(lift));
            canvas.DrawPath(shape, shadow);
            canvas.Restore();
        }
        using (var contour = new SKPaint
               {
                   Color = SKColors.Black.WithAlpha((byte)(22 + 40 * bright)), IsAntialias = true,
                   Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
               })
            canvas.DrawPath(shape, contour);
        canvas.Restore();

        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        // The picture is the blurred one over the sharp one (see GlassBackdrop.Finish).
        var height = GlassBackdrop.PictureHeight(glass);
        var toGlass = SKMatrix.CreateScaleTranslation(area.Width / glass.Width, area.Height / height, area.Left, area.Top);
        var toSharp = SKMatrix.Concat(toGlass, SKMatrix.CreateTranslation(0, -height));
        // Sharp (clear) pictures are full size; a smooth filter keeps text behind from shimmering.
        var sampling = clear ? new SKSamplingOptions(SKCubicResampler.Mitchell) : new SKSamplingOptions(SKFilterMode.Linear);
        using (var behind = glass.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, toGlass))
        using (var sharp = glass.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, toSharp))
        {
            using (var face = new SKPaint { Shader = behind })
                canvas.DrawRect(rect, face);

            // The haze: as thin as the look wants, as thick as the text needs (see Haze).
            var haze = Haze(tone, frost, darkText, tint);
            using (var wash = new SKPaint { Color = haze })
                canvas.DrawRect(rect, wash);

            // Apple's bands, at full size from about 64 points tall.
            var size = Math.Clamp(Math.Min(rect.Width, rect.Height) / 64f, 0.5f, 1f);
            var ring = Math.Min(12.5f * size, rect.Height / 2.6f);
            if (Lens != null)
            {
                var uniforms = new SKRuntimeEffectUniforms(Lens)
                {
                    ["centre"] = new[] { rect.MidX, rect.MidY },
                    ["extent"] = new[] { rect.Width / 2, rect.Height / 2 },
                    ["radius"] = radius,
                    ["ring"] = ring,
                    ["bend"] = 40f * size,
                    ["rimRing"] = 6.25f * size,
                    ["rimBend"] = 10f * size,
                    ["glow"] = 0.9f - 0.3f * bright,
                    ["crispBand"] = Math.Min(25f, rect.Height * 0.18f),
                    ["spread"] = clear ? 2.7475f : 1.59f,
                    ["shade"] = 0.05f + 0.09f * bright,
                    ["haze"] = new[] { haze.Red / 255f, haze.Green / 255f, haze.Blue / 255f, haze.Alpha / 255f },
                };
                var children = new SKRuntimeEffectChildren(Lens) { ["behind"] = behind, ["sharp"] = sharp };
                using var lens = Lens.ToShader(uniforms, children);
                // Wide enough for the crisper band near the rim (25 points) and the highlight's glow.
                using var rim = new SKPaint { Shader = lens, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 52 };
                canvas.DrawPath(shape, rim);
            }

            // A soft sheen from the top edge and a faint bright inner edge: the pane reads as a lit
            // piece of glass even over a dark or flat background, where refraction alone shows little.
            using (var sheen = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateLinearGradient(
                           new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.MidX, rect.Top + rect.Height * 0.45f),
                           [SKColors.White.WithAlpha((byte)(20 + 20 * bright)), SKColors.White.WithAlpha(0)],
                           SKShaderTileMode.Clamp),
                   })
                canvas.DrawRect(rect, sheen);
            using (var edge = new SKPaint
                   {
                       IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f,
                       Shader = SKShader.CreateLinearGradient(
                           new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.MidX, rect.Top + rect.Height * 0.6f),
                           [SKColors.White.WithAlpha((byte)(60 + 30 * bright)), SKColors.White.WithAlpha(12)],
                           SKShaderTileMode.Clamp),
                   })
            {
                var inner = SKRect.Inflate(rect, -0.8f, -0.8f);
                var rr = Math.Max(0, radius - 0.8f);
                canvas.DrawRoundRect(inner, rr, rr, edge);
            }
        }
        canvas.Restore();
    }

    /// <summary>The smoke's colour in the classic look.</summary>
    internal static readonly SKColor ClassicTint = new(0x14, 0x14, 0x16);

    /// <summary>
    /// The classic smoke's thickness: 0.22, plus up to 0.36 more the brighter the screen behind
    /// (so it stays dark glass with light text over anything), thinner below the middle of the
    /// blur slider (to 70% at none).
    /// </summary>
    internal static float ClassicTintAlpha(double brightness, float frost)
    {
        var blur = frost * 100;
        var thin = blur < 50 ? 0.7f + 0.3f * blur / 50 : 1;
        // Lighter smoke than before, so more of the screen behind comes through (dark glass, not a
        // dark slab); legibility over a bright background is still guaranteed by ClassicSmoke's Max.
        return (0.15f + 0.34f * (float)Math.Clamp(brightness, 0, 1)) * thin;
    }

    /// <summary>
    /// The classic smoke for <paramref name="tone"/>: its own formula, thickened only where the light
    /// text on it (and the dimmer secondary text) would otherwise not read, over a bright orange
    /// tab strip, say.
    /// </summary>
    internal static float ClassicSmoke(GlassTone tone, float frost) =>
        Math.Max(ClassicTintAlpha(tone.PlainLuma, frost), (float)LightHaze(tone, Ink(ClassicTint), 0));

    /// <summary>The classic look: the whole face through its shader, a sheen from the top, a hairline outline.</summary>
    internal static void DrawClassic(SKCanvas canvas, SKPath shape, SKRect rect, float radius, SKImage glass, SKRect area, GlassTone tone, float frost)
    {
        // The rim light is weaker on a thin strip, which is nearly all rim.
        var rimk = Math.Clamp((rect.Height - 4) / 16f, 0.2f, 1);
        var height = GlassBackdrop.PictureHeight(glass);
        var toGlass = SKMatrix.CreateScaleTranslation(area.Width / glass.Width, area.Height / height, area.Left, area.Top);
        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        using (var behind = glass.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear), toGlass))
        {
            var uniforms = new SKRuntimeEffectUniforms(Classic!)
            {
                ["centre"] = new[] { rect.MidX, rect.MidY },
                ["extent"] = new[] { rect.Width / 2, rect.Height / 2 },
                ["radius"] = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2),
                ["bend"] = 13f,
                ["tint"] = new[] { ClassicTint.Red / 255f, ClassicTint.Green / 255f, ClassicTint.Blue / 255f, ClassicSmoke(tone, frost) },
                ["rimk"] = rimk,
                ["top"] = rect.Top,
            };
            var children = new SKRuntimeEffectChildren(Classic!) { ["behind"] = behind };
            using var lens = Classic!.ToShader(uniforms, children);
            using var face = new SKPaint { Shader = lens, IsAntialias = true };
            canvas.DrawRect(SKRect.Inflate(rect, 2, 2), face);
        }
        // The sheen: white fading out down the first 55% of the height from the top edge, the same
        // all the way across (glass hanging from the top; on a side it would run from that side).
        using (var sheen = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Top + rect.Height * 0.55f),
                       [SKColors.White.WithAlpha((byte)(52 * rimk)), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp),
               })
            canvas.DrawPath(shape, sheen);
        canvas.Restore();
        // A hairline of light on the outline (1 point, half a point in): white, 46 at full strength.
        using var outline = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            Color = SKColors.White.WithAlpha((byte)(64 * rimk)),
        };
        var inner = SKRect.Inflate(rect, -0.5f, -0.5f);
        var r = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2) - 0.5f);
        canvas.DrawRoundRect(inner, r, r, outline);
    }
}
