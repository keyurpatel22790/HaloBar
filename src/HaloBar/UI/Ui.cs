using SkiaSharp;

namespace HaloBar.UI;

/// <summary>
/// A one-line text box drawn on the island. Typing reaches it through the overlay window,
/// which takes keyboard focus only while a field is being edited.
/// </summary>
internal sealed class TextField(string placeholder, Action<string> submit)
{
    public string Text { get; set; } = "";
    public int Caret { get; set; }
    public string Placeholder { get; set; } = placeholder;
    public Action<string> Submit { get; } = submit;
    /// <summary>
    /// Set for a field that records a shortcut instead of text: it gets every key press (with
    /// Alt combinations too) and returns true when it has what it needs and typing should end.
    /// </summary>
    public Func<int, bool>? CaptureKey { get; init; }
    /// <summary>Called whenever typing into this field stops, however it stops.</summary>
    public Action? Ended { get; init; }

    public void Insert(string s)
    {
        Text = Text.Insert(Caret, s);
        Caret += s.Length;
    }

    public void Key(int vk)
    {
        switch (vk)
        {
            case 0x08 when Caret > 0: // Backspace
                Text = Text.Remove(Caret - 1, 1);
                Caret--;
                break;
            case 0x2E when Caret < Text.Length: // Delete
                Text = Text.Remove(Caret, 1);
                break;
            case 0x25: Caret = Math.Max(0, Caret - 1); break; // Left
            case 0x27: Caret = Math.Min(Text.Length, Caret + 1); break; // Right
            case 0x24: Caret = 0; break; // Home
            case 0x23: Caret = Text.Length; break; // End
        }
    }

    public void Clear()
    {
        Text = "";
        Caret = 0;
    }
}

/// <summary>
/// Drawing and hit-testing helpers for one frame of one island. Panels draw through this so
/// they share look, fonts and click handling. Window thread only.
/// </summary>
internal sealed class Ui(Theme theme, Action<TextField> focus, Func<TextField?> focused)
{
    private readonly List<(SKRect Rect, Action Action)> _hits = [];

    public SKCanvas Canvas { get; private set; } = null!;
    public Theme Theme { get; } = theme;
    public SKPoint? Cursor { get; private set; }
    public DateTime Now { get; private set; }
    public TextField? Focused => focused();

    /// <summary>Starts typing into <paramref name="field"/> (takes keyboard focus).</summary>
    public void Focus(TextField field) => focus(field);

    public void Begin(SKCanvas canvas, SKPoint? cursor)
    {
        Canvas = canvas;
        Cursor = cursor;
        Now = DateTime.Now;
        _hits.Clear();
    }

    public void Hit(SKRect rect, Action action) => _hits.Add((rect, action));

    /// <summary>Runs the topmost action under <paramref name="p"/>. Returns false if there was none.</summary>
    public bool Click(SKPoint p)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (!_hits[i].Rect.Contains(p)) continue;
            _hits[i].Action();
            return true;
        }
        return false;
    }

    public bool Hovering(SKRect r) => Cursor is { } p && r.Contains(p);

    // ───────────────────────────── text ─────────────────────────────

    public void Text(string text, float x, float baseline, float maxWidth, float size,
        Theme.Weight weight = Theme.Weight.Regular, SKColor? color = null, SKTextAlign align = SKTextAlign.Left)
    {
        // Clean before picking a font, or an invisible mark could pull in a fallback face.
        text = Theme.Visible(text);
        using var font = Theme.Font(size, weight, text);
        Theme.DrawLine(Canvas, text, x, baseline, maxWidth, font, color ?? Theme.Text, align);
    }

    public void Wrapped(string text, float x, float top, float maxWidth, int maxLines, float size, SKColor? color = null)
    {
        text = Theme.Visible(text);
        using var font = Theme.Font(size, Theme.Weight.Regular, text);
        Theme.DrawWrapped(Canvas, text, x, top, maxWidth, maxLines, font, color ?? Theme.Text);
    }

    public float Measure(string text, float size, Theme.Weight weight = Theme.Weight.Regular)
    {
        text = Theme.Visible(text);
        using var font = Theme.Font(size, weight, text);
        return font.MeasureText(text);
    }

    // ───────────────────────────── shapes ─────────────────────────────

    public void Fill(SKRect r, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        Canvas.DrawRoundRect(r, radius, radius, paint);
    }

    public void Circle(float cx, float cy, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        Canvas.DrawCircle(cx, cy, radius, paint);
    }

    public void Ring(float cx, float cy, float radius, SKColor color, float width = 2)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };
        Canvas.DrawCircle(cx, cy, radius, paint);
    }

    /// <summary>A progress arc starting at twelve o'clock.</summary>
    public void Arc(float cx, float cy, float radius, float fraction, SKColor color, float width = 3, SKColor? track = null)
    {
        var box = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round };
        paint.Color = track ?? Theme.Faint;
        Canvas.DrawOval(box, paint);
        if (fraction <= 0) return;
        paint.Color = color;
        Canvas.DrawArc(box, -90, 360 * Math.Clamp(fraction, 0, 1), false, paint);
    }

    /// <summary>A horizontal bar: a faint track with a filled part.</summary>
    public void Bar(SKRect track, float fraction, SKColor color)
    {
        var r = track.Height / 2;
        Fill(track, r, Theme.Faint);
        if (fraction <= 0) return;
        Fill(new SKRect(track.Left, track.Top, track.Left + Math.Max(track.Height, track.Width * Math.Clamp(fraction, 0, 1)), track.Bottom), r, color);
    }

    public void Image(SKImage image, SKRect dest, float radius)
    {
        Canvas.Save();
        Canvas.ClipRoundRect(new SKRoundRect(dest, radius), SKClipOperation.Intersect, true);
        // Crop to the destination's aspect ratio rather than stretching.
        var scale = Math.Max(dest.Width / image.Width, dest.Height / image.Height);
        var sw = dest.Width / scale;
        var sh = dest.Height / scale;
        var src = SKRect.Create((image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh);
        using var paint = new SKPaint { IsAntialias = true };
        Canvas.DrawImage(image, src, dest, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        Canvas.Restore();
    }

    // ───────────────────────────── controls ─────────────────────────────

    /// <summary>A rounded label button. <paramref name="on"/> draws it selected.</summary>
    public SKRect Chip(float x, float midY, string label, Action onClick, bool on = false, float size = 12, bool accent = false)
    {
        var w = Measure(label, size) + 22;
        var r = new SKRect(x, midY - 11, x + w, midY + 11);
        var bg = accent ? Theme.Accent : Theme.Overlay(on ? (byte)36 : Hovering(r) ? (byte)22 : (byte)12);
        Fill(r, 11, bg);
        Text(label, r.MidX, midY + size * 0.36f, w, size, Theme.Weight.Regular,
            accent ? Theme.OnAccent : on ? Theme.Text : Theme.Dim, SKTextAlign.Center);
        Hit(r, onClick);
        return r;
    }

    /// <summary>A round icon button.</summary>
    public void IconButton(float cx, float cy, float radius, Action<SKCanvas, float, float, float, SKColor> icon, Action onClick,
        bool filled = false, bool enabled = true)
    {
        var box = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        if (filled) Circle(cx, cy, radius, Theme.Text);
        else if (Hovering(box) && enabled) Circle(cx, cy, radius, Theme.Overlay(22));
        icon(Canvas, cx, cy, radius * 0.9f, filled ? Theme.Background : enabled ? Theme.Text : Theme.Faint);
        if (enabled) Hit(box, onClick);
    }

    /// <summary>An on/off switch.</summary>
    public void Switch(float x, float midY, bool on, Action onClick)
    {
        var r = new SKRect(x, midY - 9, x + 32, midY + 9);
        Fill(r, 9, on ? Theme.Accent : Theme.Overlay(40));
        Circle(on ? r.Right - 9 : r.Left + 9, midY, 7, on ? Theme.OnAccent : Theme.Text);
        Hit(r, onClick);
    }

    /// <summary>A text box. Clicking it starts typing into <paramref name="field"/>.</summary>
    public void Field(TextField field, SKRect r, float size = 12.5f)
    {
        var editing = Focused == field;
        Fill(r, r.Height / 2, Theme.Overlay(editing ? (byte)30 : Hovering(r) ? (byte)20 : (byte)12));
        var x = r.Left + 12;
        var baseline = r.MidY + size * 0.36f;
        var maxWidth = r.Width - 24;
        if (field.Text.Length == 0 && !editing)
            Text(field.Placeholder, x, baseline, maxWidth, size, Theme.Weight.Regular, Theme.Faint);
        else
        {
            using var font = Theme.Font(size, Theme.Weight.Regular, field.Text);
            // Keep the caret in view by scrolling long text to the left.
            var before = font.MeasureText(field.Text[..Math.Min(field.Caret, field.Text.Length)]);
            var shift = Math.Max(0, before - maxWidth + 4);
            Canvas.Save();
            Canvas.ClipRect(new SKRect(x - 2, r.Top, r.Right - 10, r.Bottom));
            using var paint = new SKPaint { Color = Theme.Text, IsAntialias = true };
            Canvas.DrawText(field.Text, x - shift, baseline, SKTextAlign.Left, font, paint);
            if (editing && (int)(Now.Millisecond / 500.0) == 0)
            {
                paint.Color = Theme.Accent;
                Canvas.DrawRect(x - shift + before, r.MidY - size * 0.6f, 1.5f, size * 1.2f, paint);
            }
            Canvas.Restore();
        }
        Hit(r, () => focus(field));
    }

    public void Empty(SKRect body, string headline, string hint)
    {
        Text(headline, body.MidX, body.MidY - 4, body.Width - 40, 15, Theme.Weight.SemiBold, Theme.Dim, SKTextAlign.Center);
        Text(hint, body.MidX, body.MidY + 18, body.Width - 40, 12, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }

    public static SKRect Square(float cx, float cy, float half) => new(cx - half, cy - half, cx + half, cy + half);
    public static SKRect Inflate(SKRect r, float by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    public static string Ago(DateTime time)
    {
        var d = DateTime.Now - time;
        if (d.TotalSeconds < 45) return "now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
