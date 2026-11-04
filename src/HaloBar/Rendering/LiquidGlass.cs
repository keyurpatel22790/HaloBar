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
/// <item>Light from the top left catches a thin line along the outline, brightest on the corner
/// facing it and fainter on the opposite one, as a lift of the colour behind rather than flat
/// white; the bevel facing away is a shade darker, more so over a bright background, where that
/// shading is what shows the glass's thickness.</item>
/// <item>It adapts at every blur: a white haze under dark text over a bright background, a dark one
/// under light text, each as thin as the look wants and as thick as the text needs.</item>
/// <item>Outside: a soft shadow, and a hairline darker contour that keeps the edge on white.</item>
/// </list>
/// Written for HaloBar from published descriptions of the look.
/// </summary>
internal static class LiquidGlass
{
    private const string LensSksl = @"
        uniform shader behind;
        uniform float2 centre;
        uniform float2 extent;   // half the glass's width and height
        uniform float radius;
        uniform float ring;      // the bevel's width, in from the outline
        uniform float bend;      // how far the picture is pulled in, right at the outline
        uniform float glow;      // rim light strength
        uniform float shade;     // how much the bevel facing away darkens
        uniform half4 haze;      // the face's haze (unpremultiplied), laid on before the light
        float sdf(float2 p) {
            float2 q = abs(p - centre) - (extent - radius);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }
        half4 main(float2 p) {
            float depth = max(-sdf(p), 0.0);
            float2 grad = float2(sdf(p + float2(0.75, 0.0)) - sdf(p - float2(0.75, 0.0)),
                                 sdf(p + float2(0.0, 0.75)) - sdf(p - float2(0.0, 0.75)));
            float2 n = grad / max(length(grad), 0.0001);
            // Snell through the bevel: none where it meets the face, the most at the outline.
            float x = clamp(1.0 - depth / ring, 0.0, 1.0);
            float ti = asin(x * x);
            float tt = asin(sin(ti) / 1.5);
            float pull = tan(ti - tt) / 1.118;
            float2 o = -n * bend * pull;
            half3 c;
            c.r = behind.eval(p + o * 0.98).r;
            c.g = behind.eval(p + o).g;
            c.b = behind.eval(p + o * 1.02).b;
            c = mix(c, haze.rgb, haze.a);
            // Light along the diagonal: it gathers into crescents on the curves facing it (top
            // left) and, fainter, opposite (bottom right), and fades along the straight edges.
            float2 toLight = normalize(float2(-1.0, -1.0));
            float d = dot(n, toLight);
            float lit = pow(max(d, 0.0), 5.0) + 0.6 * pow(max(-d, 0.0), 5.0);
            // A thin line right on the outline, and a soft glow across the lit part of the bevel.
            float line = 1.0 - smoothstep(0.3, 1.7, depth);
            float soft = (1.0 - smoothstep(0.0, ring * 0.55, depth)) * 0.38;
            float s = clamp(lit * (line * 1.1 + soft) * glow + line * 0.22, 0.0, 1.0);
            c = mix(c, min(c * 1.3 + 0.75, 1.0), half(s));
            // The bevel's shading: darker facing away from the light and toward the outline.
            float away = 0.35 + 0.65 * max(-d, 0.0);
            c *= half(1.0 - shade * pull * away);
            return half4(c, 1.0);
        }";

    private static readonly SKRuntimeEffect? Lens = Build();

    /// <summary>The lens shader built (false: it didn't compile, and the glass is drawn flat).</summary>
    public static bool Ready => Lens != null;

    private static SKRuntimeEffect? Build()
    {
        var effect = SKRuntimeEffect.CreateShader(LensSksl, out var errors);
        if (effect == null) Log.Write($"Glass: lens shader didn't build: {errors}");
        return effect;
    }

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
        var lightAlpha = LightHaze(tone, Ink(under), IsClear(frost) ? 0.18 : LightFloor(frost));
        var forLight = under.WithAlpha((byte)Math.Round(255 * lightAlpha));
        if (darkText <= 0.001f) return forLight;
        var forDark = SKColors.White.WithAlpha((byte)Math.Round(255 * DarkHaze(tone, DarkFloor(frost))));
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
        var clear = IsClear(frost);
        var bright = (float)Math.Clamp((tone.Mean - 0.35) / 0.45, 0, 1);

        // Outside only: inside, a shadow would darken what shows through.
        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Difference, true);
        using (var shadow = new SKPaint
               {
                   Color = SKColors.Black.WithAlpha((byte)(30 + 26 * bright + 40 * lift)), IsAntialias = true,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7 + 11 * lift),
               })
        {
            canvas.Save();
            canvas.Translate(0, 3 + 7 * lift);
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
        var toGlass = SKMatrix.CreateScaleTranslation(area.Width / glass.Width, area.Height / glass.Height, area.Left, area.Top);
        // Sharp (clear) pictures are full size; a smooth filter keeps text behind from shimmering.
        var sampling = clear ? new SKSamplingOptions(SKCubicResampler.Mitchell) : new SKSamplingOptions(SKFilterMode.Linear);
        using (var behind = glass.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, toGlass))
        {
            using (var face = new SKPaint { Shader = behind })
                canvas.DrawRect(rect, face);

            // The haze: as thin as the look wants, as thick as the text needs (see Haze).
            var haze = Haze(tone, frost, darkText, tint);
            using (var wash = new SKPaint { Color = haze })
                canvas.DrawRect(rect, wash);

            var ring = Math.Min(24, rect.Height / 2.2f);
            if (Lens != null)
            {
                var uniforms = new SKRuntimeEffectUniforms(Lens)
                {
                    ["centre"] = new[] { rect.MidX, rect.MidY },
                    ["extent"] = new[] { rect.Width / 2, rect.Height / 2 },
                    ["radius"] = radius,
                    ["ring"] = ring,
                    ["bend"] = ring * 2.4f,
                    ["glow"] = 1f - 0.25f * bright,
                    ["shade"] = 0.05f + 0.09f * bright,
                    ["haze"] = new[] { haze.Red / 255f, haze.Green / 255f, haze.Blue / 255f, haze.Alpha / 255f },
                };
                var children = new SKRuntimeEffectChildren(Lens) { ["behind"] = behind };
                using var lens = Lens.ToShader(uniforms, children);
                using var rim = new SKPaint { Shader = lens, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ring * 2 };
                canvas.DrawPath(shape, rim);
            }
        }
        canvas.Restore();
    }
}
