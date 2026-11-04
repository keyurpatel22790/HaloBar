using HaloBar.Rendering;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace HaloBar.UI;

/// <summary>
/// Colours, fonts and text helpers shared by every island. The colours come from the chosen
/// surface and accent; they glide to a new choice instead of jumping, and some looks keep
/// moving (Aurora, Rainbow) or follow the album art of what is playing.
/// </summary>
internal sealed class Theme : IDisposable
{
    private const double GlideRate = 9; // per second; about a third of a second to settle

    private SKColor _top, _bottom, _edge, _text, _accent;
    private SKColor _windowsAccent = Looks.WindowsAccent();

    public SurfaceLook Surface { get; private set; } = Looks.Surfaces[0];
    public AccentLook AccentChoice { get; private set; } = Looks.Accents[0];
    /// <summary>The music card shows the cover's colours in soft streaks behind it.</summary>
    public bool AlbumCard { get; private set; }
    public ArtPalette? Palette { get; private set; }
    public double Time { get; private set; }

    /// <summary>Colours are still gliding toward the chosen look.</summary>
    public bool Transitioning { get; private set; }
    /// <summary>The look moves by itself, so it needs frames even when nothing else changes.</summary>
    public bool Animated => Surface.Effect == SurfaceEffect.Aurora || AccentChoice.Source == AccentSource.Rainbow;

    public SKColor Top => Frosted(_top);
    public SKColor Background => Frosted(_bottom);

    /// <summary>
    /// On a see-through surface the tint over the blurred desktop gets thinner (clearer) or
    /// thicker (frostier) with Glass blur; Windows' own blur strength is fixed.
    /// </summary>
    private SKColor Frosted(SKColor c) => Surface.SeeThrough
        ? c.WithAlpha((byte)Math.Clamp(c.Alpha * (0.35f + Prefs.GlassFrost * 1.3f), 0, 255))
        : c;
    public SKColor Edge => _edge;
    public SKColor Text => TextOverride ?? _text;
    public SKColor Dim => Text.WithAlpha(Prefs.HighContrast ? (byte)0xD0 : (byte)0x99);
    public SKColor Faint => Text.WithAlpha(Prefs.HighContrast ? (byte)0x80 : (byte)0x33);

    /// <summary>
    /// Text in another colour while a window with a look of its own draws (Settings follows Windows'
    /// light or dark mode, not what's behind the island); null for the island's own.
    /// </summary>
    public SKColor? TextOverride { get; set; }
    /// <summary>
    /// The accent. On light glass (dark text) it's deepened as far as it takes to read as text
    /// there (a bright yellow becomes amber): accents label things, on any background.
    /// </summary>
    public SKColor Accent => (Surface.Effect == SurfaceEffect.Lens || TextOverride != null) && DarkText ? Deepened(_accent) : _accent;
    /// <summary>Text or icons drawn on top of the accent colour.</summary>
    public SKColor OnAccent => Luminance(Accent) > 0.55 ? new SKColor(0x16, 0x16, 0x18) : SKColors.White;

    private SKColor _deepenedFrom, _deepened;

    /// <summary><paramref name="c"/> darkened (keeping its hue) until it has 3:1 contrast on near-white glass.</summary>
    internal SKColor Deepened(SKColor c)
    {
        if (c == _deepenedFrom) return _deepened;
        var d = c;
        for (var i = 0; i < 40 && LiquidGlass.Contrast(LiquidGlass.Grey(Lum(d)), 0.9) < 3.2; i++)
            d = new SKColor((byte)(d.Red * 0.93), (byte)(d.Green * 0.93), (byte)(d.Blue * 0.93), d.Alpha);
        (_deepenedFrom, _deepened) = (c, d);
        return d;
    }

    private static double Lum(SKColor c) =>
        0.2126 * LiquidGlass.Linear(c.Red / 255.0) + 0.7152 * LiquidGlass.Linear(c.Green / 255.0) + 0.0722 * LiquidGlass.Linear(c.Blue / 255.0);
    /// <summary>A wash of the text colour, for hover and selection fills on any surface.</summary>
    public SKColor Overlay(byte alpha) => Text.WithAlpha(alpha);

    /// <summary>Raised when the user picks a different look, so it can be saved.</summary>
    public event Action? Changed;

    private readonly SKTypeface _regular, _semibold, _bold;
    private readonly Dictionary<int, SKTypeface> _fallbacks = [];

    public Theme(string? surface, string? accent, bool albumCard)
    {
        Surface = Looks.Surface(surface);
        AccentChoice = Looks.Accent(accent);
        AlbumCard = albumCard;
        (_top, _bottom, _edge, _text, _accent) = Targets();
        _regular = Family(SKFontStyle.Normal);
        _semibold = Family(new SKFontStyle(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        _bold = Family(SKFontStyle.Bold);
    }

    public void Choose(SurfaceLook? surface = null, AccentLook? accent = null, bool? albumCard = null)
    {
        Surface = surface ?? Surface;
        AccentChoice = accent ?? AccentChoice;
        AlbumCard = albumCard ?? AlbumCard;
        Changed?.Invoke();
    }

    /// <summary>The Windows accent colour may have changed in Personalization settings.</summary>
    public void RefreshWindowsAccent() => _windowsAccent = Looks.WindowsAccent();

    public SKColor WindowsAccentColor => _windowsAccent;

    /// <summary>Moves the colours one frame toward the chosen look. Call once per frame for all islands.</summary>
    public void Tick(double dt, ArtPalette? palette)
    {
        Time += dt;
        Palette = palette;
        var (top, bottom, edge, text, accent) = Targets();
        var k = (float)(1 - Math.Exp(-dt * GlideRate));
        _top = Glide(_top, top, k);
        _bottom = Glide(_bottom, bottom, k);
        _edge = Glide(_edge, edge, k);
        // Text crossfades in about a quarter second, starting the frame the background crosses
        // over; the glass's haze follows it.
        _text = Glide(_text, text, (float)(1 - Math.Exp(-dt * TextRate)));
        // The brightness behind, eased just enough that the glass's tint never steps.
        if (BehindTone is { } tone) SmoothTone = SmoothTone.Toward(tone, 1 - Math.Exp(-dt * ToneRate));
        _accent = Glide(_accent, accent, k);
        Transitioning = Far(_top, top) || Far(_bottom, bottom) || Far(_edge, edge) || Far(_text, text)
                        || (!Animated && Far(_accent, accent))
                        || (BehindTone is { } b && (Math.Abs(b.Mean - SmoothTone.Mean) > 0.004 || Math.Abs(b.High - SmoothTone.High) > 0.004
                                                     || Math.Abs(b.Low - SmoothTone.Low) > 0.004));
    }

    private (SKColor Top, SKColor Bottom, SKColor Edge, SKColor Text, SKColor Accent) Targets()
    {
        var s = Surface;
        SKColor top = s.Top, bottom = s.Bottom, edge = s.Edge, own = s.Accent;
        switch (s.Effect)
        {
            case SurfaceEffect.Album when Palette != null:
                Palette.Deep.ToHsv(out var h, out var sat, out _);
                top = SKColor.FromHsv(h, sat, 24);
                bottom = Palette.Deep;
                edge = Palette.Vibrant.WithAlpha(90);
                own = Palette.Vibrant;
                break;
            case SurfaceEffect.Aurora:
                var hue = (float)(Time * 14 % 360);
                top = SKColor.FromHsv(hue, 55, 17);
                bottom = SKColor.FromHsv((hue + 70) % 360, 55, 9);
                own = SKColor.FromHsv((hue + 35) % 360, 55, 98);
                break;
        }
        var accent = AccentChoice.Source switch
        {
            AccentSource.Surface => own,
            AccentSource.Windows => _windowsAccent,
            AccentSource.Album => Palette?.Vibrant ?? own,
            AccentSource.Rainbow => SKColor.FromHsv((float)(Time * 45 % 360), 65, 100),
            _ => AccentChoice.Color,
        };
        return (top, bottom, edge, TextFor(s), accent);
    }

    /// <summary>
    /// How bright the screen behind the glass is (0–1), from the picture the lens reads; null when
    /// no surface reads it. Auto text turns dark over a bright background and light over a dark one.
    /// </summary>
    public double? BehindBrightness => BehindTone?.Mean;

    /// <summary>How bright the screen behind is, and its darkest and brightest parts; null when no surface reads it.</summary>
    public GlassTone? BehindTone { get; set; }

    /// <summary><see cref="BehindTone"/> eased over about a tenth of a second.</summary>
    public GlassTone SmoothTone { get; private set; } = GlassTone.Dark;

    /// <summary>Per second: text crossfades in about a quarter second; the tone settles in about a tenth.</summary>
    internal const double TextRate = 12, ToneRate = 22;
    private bool _darkOverGlass;

    /// <summary>Text is dark on light (so washes and shadows should be light).</summary>
    public bool DarkText => Luminance(Text) < 0.5;

    private SKColor TextFor(SurfaceLook s)
    {
        switch (Prefs.TextColour)
        {
            case "white": return new SKColor(0xF5, 0xF5, 0xF7);
            case "black": return new SKColor(0x16, 0x16, 0x1A);
            case var hex when hex.StartsWith('#') && SKColor.TryParse(hex, out var custom): return custom;
        }
        if (s.Effect != SurfaceEffect.Lens || BehindTone is not { } tone) return s.Text;
        // At once, no waiting: the gap between the two switch points keeps it from flickering,
        // and the glass's haze keeps either colour readable meanwhile.
        _darkOverGlass = LiquidGlass.WantsDarkText(tone, Prefs.GlassFrost, _darkOverGlass);
        return _darkOverGlass ? LiquidGlass.DarkInk : LiquidGlass.LightInk;
    }

    /// <summary>A soft shadow under text helps it read on see-through and busy backgrounds.</summary>
    public bool TextShadows => Prefs.TextShadow switch { "on" => true, "off" => false, _ => Surface.SeeThrough };

    /// <summary>The surface fill: a top-to-bottom gradient, or null when the surface is one flat colour.</summary>
    public SKShader? BackgroundShader(SKRect r) => _top == _bottom ? null
        : SKShader.CreateLinearGradient(new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Bottom), [_top, _bottom], SKShaderTileMode.Clamp);

    /// <summary>Aurora's edge: a ring of colours that slowly turns. Null for other surfaces.</summary>
    public SKShader? EdgeShader(SKRect r)
    {
        if (Surface.Effect == SurfaceEffect.Lens)
        {
            // Bright where light hits the top, fading down the sides, a faint reflection at the bottom.
            return SKShader.CreateLinearGradient(new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Bottom),
                [SKColors.White.WithAlpha(150), SKColors.White.WithAlpha(34), SKColors.White.WithAlpha(22), SKColors.White.WithAlpha(80)],
                [0, 0.35f, 0.7f, 1], SKShaderTileMode.Clamp);
        }
        if (Surface.Effect != SurfaceEffect.Aurora) return null;
        var hue = (float)(Time * 14 % 360);
        var colors = Enumerable.Range(0, 5).Select(i => SKColor.FromHsv((hue + i * 72) % 360, 60, 100).WithAlpha(170)).ToArray();
        colors[^1] = colors[0];
        var rotate = SKMatrix.CreateRotationDegrees((float)(Time * 40 % 360), r.MidX, r.MidY);
        return SKShader.CreateSweepGradient(new SKPoint(r.MidX, r.MidY), colors, null, SKShaderTileMode.Clamp, 0, 360, rotate);
    }

    /// <summary>The glass lens's sheen: soft light across the top of the shape, inside it.</summary>
    public void Sheen(SKCanvas canvas, SKPath shape, SKRect r)
    {
        if (Surface.Effect != SurfaceEffect.Lens) return;
        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        var depth = Math.Min(r.Height * 0.55f, 46);
        using var sheen = SKShader.CreateLinearGradient(new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Top + depth),
            [SKColors.White.WithAlpha(40), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = sheen, IsAntialias = true };
        canvas.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + depth), paint);
        // A soft glint near the top-left, like light caught in curved glass.
        using var glint = SKShader.CreateRadialGradient(new SKPoint(r.Left + r.Width * 0.18f, r.Top + 4), Math.Max(24, r.Width * 0.22f),
            [SKColors.White.WithAlpha(36), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp);
        paint.Shader = glint;
        canvas.DrawRect(r, paint);
        canvas.Restore();
    }

    internal static SKColor Glide(SKColor from, SKColor to, float k) => new(
        (byte)Math.Round(from.Red + (to.Red - from.Red) * k),
        (byte)Math.Round(from.Green + (to.Green - from.Green) * k),
        (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * k),
        (byte)Math.Round(from.Alpha + (to.Alpha - from.Alpha) * k));

    private static bool Far(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) + Math.Abs(a.Alpha - b.Alpha) > 3;

    private static double Luminance(SKColor c) => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;

    private static SKTypeface Family(SKFontStyle style)
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            var tf = SKTypeface.FromFamilyName(name, style);
            if (tf != null && tf.FamilyName.StartsWith("Segoe", StringComparison.Ordinal)) return tf;
        }
        return SKTypeface.Default;
    }

    public enum Weight { Regular, SemiBold, Bold }

    /// <summary>
    /// A font for <paramref name="text"/>. Song titles and notifications are often in scripts
    /// Segoe UI lacks (Devanagari, CJK, emoji), so ask Windows for a face that has them.
    /// </summary>
    public SKFont Font(float size, Weight weight = Weight.Regular, string? text = null)
    {
        var face = weight switch { Weight.Bold => _bold, Weight.SemiBold => _semibold, _ => _regular };
        if (!string.IsNullOrEmpty(text))
        {
            var missing = FirstMissing(face, text);
            if (missing >= 0)
            {
                if (!_fallbacks.TryGetValue(missing, out var fallback))
                {
                    fallback = SKFontManager.Default.MatchCharacter(face.FamilyName, face.FontStyle, null, missing) ?? face;
                    _fallbacks[missing] = fallback;
                }
                face = fallback;
            }
        }
        return new SKFont(face, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
    }

    private static int FirstMissing(SKTypeface face, string text)
    {
        using var font = new SKFont(face);
        for (var i = 0; i < text.Length; i++)
        {
            int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            if (char.IsSurrogatePair(text, i)) i++;
            if (cp < 0x0250) continue; // Latin: Segoe has it; everything else is checked (Indic is below U+2000 too)
            if (!font.ContainsGlyph(cp)) return cp;
        }
        return -1;
    }

    /// <summary>Draws one line of text, cut with an ellipsis to fit <paramref name="maxWidth"/>.</summary>
    public void DrawLine(SKCanvas canvas, string text, float x, float baseline, float maxWidth, SKFont font, SKColor color,
        SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 4) return;
        // The shadow: the same text a touch lower in the opposite tone, faint, under the real one.
        if (TextShadows && color.Alpha > 60)
        {
            var light = Luminance(color) > 0.5;
            var shade = light ? SKColors.Black.WithAlpha((byte)(color.Alpha * 0.45f)) : SKColors.White.WithAlpha((byte)(color.Alpha * 0.5f));
            DrawLineOnce(canvas, text, x, baseline + Math.Max(1, font.Size / 14), maxWidth, font, shade, align);
        }
        DrawLineOnce(canvas, text, x, baseline, maxWidth, font, color, align);
    }

    private void DrawLineOnce(SKCanvas canvas, string text, float x, float baseline, float maxWidth, SKFont font, SKColor color,
        SKTextAlign align)
    {
        text = Visible(text).ReplaceLineEndings(" ");
        var fitted = Fit(text, maxWidth, font);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        if (NeedsShaping(fitted))
        {
            // Indic, Arabic and Thai letters change shape and order by context; HarfBuzz does that.
            if (!_shapers.TryGetValue(font.Typeface, out var shaper))
                _shapers[font.Typeface] = shaper = new SKShaper(font.Typeface);
            var runs = DirectionRuns(fitted);
            if (runs.Count == 1)
            {
                canvas.DrawShapedText(shaper, fitted, x, baseline, align, font, paint);
                return;
            }
            // Mixed directions: shape each run on its own and lay the runs out in reading order.
            var widths = runs.Select(r => shaper.Shape(r.Text, font).Width).ToList();
            var total = widths.Sum();
            var left = align switch { SKTextAlign.Center => x - total / 2, SKTextAlign.Right => x - total, _ => x };
            var order = Enumerable.Range(0, runs.Count);
            if (runs[0].Rtl) order = order.Reverse();
            foreach (var i in order)
            {
                canvas.DrawShapedText(shaper, runs[i].Text, left, baseline, SKTextAlign.Left, font, paint);
                left += widths[i];
            }
        }
        else canvas.DrawText(fitted, x, baseline, align, font, paint);
    }

    private static bool IsRtl(char ch) => ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>
    /// Splits a line into right-to-left (Hebrew, Arabic) and left-to-right runs. Spaces and
    /// punctuation stay with the run before them; a line that opens with them joins the first run.
    /// </summary>
    public static List<(string Text, bool Rtl)> DirectionRuns(string text)
    {
        var runs = new List<(string Text, bool Rtl)>();
        var start = 0;
        bool? rtl = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!char.IsLetterOrDigit(ch)) continue; // neutral
            var here = IsRtl(ch);
            if (rtl == null) rtl = here;
            else if (here != rtl)
            {
                runs.Add((text[start..i], rtl.Value));
                start = i;
                rtl = here;
            }
        }
        runs.Add((text[start..], rtl ?? false));
        return runs;
    }

    private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];

    /// <summary>True for scripts whose glyphs need shaping: Hebrew to Sinhala, Thai, Lao, Myanmar.</summary>
    public static bool NeedsShaping(string text)
    {
        foreach (var ch in text)
            if (ch is >= '֐' and <= '෿' or >= '฀' and <= '໿' or >= 'က' and <= '႟') return true;
        return false;
    }

    /// <summary>Wraps text into at most <paramref name="maxLines"/> lines, the last one ellipsised.</summary>
    public void DrawWrapped(SKCanvas canvas, string text, float x, float top, float maxWidth, int maxLines, SKFont font, SKColor color)
    {
        var lineHeight = font.Size * 1.3f;
        var shown = WrapLines(text, maxWidth, maxLines, font);
        for (var i = 0; i < shown.Count; i++)
            DrawLine(canvas, shown[i], x, top + font.Size + i * lineHeight, maxWidth, font, color);
    }

    /// <summary>The lines <paramref name="text"/> wraps into at <paramref name="maxWidth"/>: at most <paramref name="maxLines"/>, the last ellipsised if there was more.</summary>
    public static List<string> WrapLines(string text, float maxWidth, int maxLines, SKFont font)
    {
        // Drawn text is shaped (kerning, ligatures, fallback fonts) and can come out a little wider
        // than measured here; a small margin keeps a wrapped line from being cut short with "…".
        maxWidth *= 0.96f;
        var lines = new List<string>();
        foreach (var para in Visible(text).Replace("\r", "").Split('\n'))
        {
            var words = para.Split(' ');
            var line = "";
            foreach (var word in words)
            {
                var attempt = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(attempt) <= maxWidth || line.Length == 0) line = attempt;
                else
                {
                    lines.Add(line);
                    line = word;
                }
                if (lines.Count > maxLines) break;
            }
            lines.Add(line);
            if (lines.Count > maxLines) break;
        }
        var shown = lines.Take(maxLines).ToList();
        if (lines.Count > maxLines && shown.Count > 0) shown[^1] += "…";
        return shown;
    }

    /// <summary>
    /// Drops invisible formatting characters: direction marks and isolates (Phone Link wraps
    /// names in U+2068…U+2069), zero-width spaces and byte-order marks. Fonts have no glyph for
    /// them, so they would otherwise draw as a thin bar. The zero-width joiner stays, because
    /// emoji sequences need it.
    /// </summary>
    public static string Visible(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var keep = true;
        foreach (var ch in text)
            if (ch != '‍' && char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) { keep = false; break; }
        if (keep) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch == '‍' || char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format) sb.Append(ch);
        return sb.ToString();
    }

    public static string Fit(string text, float maxWidth, SKFont font)
    {
        text = Visible(text);
        if (font.MeasureText(text) <= maxWidth) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (font.MeasureText(text[..mid] + "…") <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? "…" : text[..lo].TrimEnd() + "…";
    }

    public void Dispose()
    {
        _regular.Dispose();
        _semibold.Dispose();
        _bold.Dispose();
        foreach (var shaper in _shapers.Values) shaper.Dispose();
    }
}
