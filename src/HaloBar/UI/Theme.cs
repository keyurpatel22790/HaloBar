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

    public SKColor Top => _top;
    public SKColor Background => _bottom;
    public SKColor Edge => _edge;
    public SKColor Text => _text;
    public SKColor Dim => _text.WithAlpha(0x99);
    public SKColor Faint => _text.WithAlpha(0x33);
    public SKColor Accent => _accent;
    /// <summary>Text or icons drawn on top of the accent colour.</summary>
    public SKColor OnAccent => Luminance(_accent) > 0.55 ? new SKColor(0x16, 0x16, 0x18) : SKColors.White;
    /// <summary>A wash of the text colour, for hover and selection fills on any surface.</summary>
    public SKColor Overlay(byte alpha) => _text.WithAlpha(alpha);

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
        _text = Glide(_text, text, k);
        _accent = Glide(_accent, accent, k);
        Transitioning = Far(_top, top) || Far(_bottom, bottom) || Far(_edge, edge) || Far(_text, text)
                        || (!Animated && Far(_accent, accent));
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
        return (top, bottom, edge, s.Text, accent);
    }

    /// <summary>The surface fill: a top-to-bottom gradient, or null when the surface is one flat colour.</summary>
    public SKShader? BackgroundShader(SKRect r) => _top == _bottom ? null
        : SKShader.CreateLinearGradient(new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Bottom), [_top, _bottom], SKShaderTileMode.Clamp);

    /// <summary>Aurora's edge: a ring of colours that slowly turns. Null for other surfaces.</summary>
    public SKShader? EdgeShader(SKRect r)
    {
        if (Surface.Effect != SurfaceEffect.Aurora) return null;
        var hue = (float)(Time * 14 % 360);
        var colors = Enumerable.Range(0, 5).Select(i => SKColor.FromHsv((hue + i * 72) % 360, 60, 100).WithAlpha(170)).ToArray();
        colors[^1] = colors[0];
        var rotate = SKMatrix.CreateRotationDegrees((float)(Time * 40 % 360), r.MidX, r.MidY);
        return SKShader.CreateSweepGradient(new SKPoint(r.MidX, r.MidY), colors, null, SKShaderTileMode.Clamp, 0, 360, rotate);
    }

    private static SKColor Glide(SKColor from, SKColor to, float k) => new(
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
        for (var i = 0; i < shown.Count; i++)
            DrawLine(canvas, shown[i], x, top + font.Size + i * lineHeight, maxWidth, font, color);
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
