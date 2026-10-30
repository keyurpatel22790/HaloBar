using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>The music card: artwork, title, progress (styled, seekable), the line being sung, controls.</summary>
internal sealed class MediaPanel(Hub hub, Visualizer visualizer) : Panel(hub)
{
    public override string Title => "Music";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Note(c, cx, cy, size, color);

    private MediaSnapshot? Media => Hub.Media.Snapshot is { } m && !string.IsNullOrWhiteSpace(m.Title) ? m : null;

    public override double FrameInterval =>
        Media is { IsPlaying: true } || (Theme.AlbumCard && Media?.Palette != null) ? 1 / 30.0 : 0.5;

    public override void Background(Ui ui, SKRect r)
    {
        if (!Theme.AlbumCard || Media?.Palette is not { } palette) return;
        // The cover's colours as soft, slowly drifting light behind the card.
        var t = (float)Theme.Time;
        var alpha = (byte)(Theme.Surface.IsLight ? 120 : 150);
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < palette.Swatches.Length; i++)
        {
            var color = palette.Swatches[i];
            var center = new SKPoint(
                r.Left + r.Width * (0.18f + 0.32f * i) + MathF.Sin(t * 0.35f + i * 2.1f) * 46,
                r.Top + r.Height * (0.35f + 0.3f * (i % 2)) + MathF.Cos(t * 0.27f + i * 1.3f) * 24);
            using var glow = SKShader.CreateRadialGradient(center, r.Height * 0.95f, [color.WithAlpha(alpha), color.WithAlpha(0)], SKShaderTileMode.Clamp);
            paint.Shader = glow;
            ui.Canvas.DrawRect(r, paint);
        }
        // A veil of the surface colour keeps text readable over bright covers.
        paint.Shader = null;
        paint.Color = Theme.Background.WithAlpha(Theme.Surface.IsLight ? (byte)70 : (byte)95);
        ui.Canvas.DrawRect(r, paint);
    }

    /// <summary>Any app → the app playing now → each other app with a session → any app.</summary>
    private void CycleSource(MediaSnapshot m)
    {
        var sources = Hub.Media.Sources.Select(s => s.Id).ToList();
        var current = Hub.Settings.MusicSource;
        string next;
        if (current.Length == 0) next = sources.FirstOrDefault(id => Hub.Media.Sources.First(s => s.Id == id).Name == m.SourceApp) ?? sources.FirstOrDefault() ?? "";
        else
        {
            var i = sources.IndexOf(current);
            next = i >= 0 && i + 1 < sources.Count ? sources[i + 1] : "";
        }
        Hub.Media.Choose(next);
        Hub.ShowHud(new Hud(HudIcon.Info, next.Length == 0 ? "Music from any app"
            : $"Music from {Hub.Media.Sources.FirstOrDefault(s => s.Id == next).Name}"));
    }

    public override void Draw(Ui ui, SKRect body)
    {
        var m = Media;
        if (m == null)
        {
            ui.Empty(body, "Nothing playing", "Play something in Spotify, a browser or Media Player");
            return;
        }
        var settings = Hub.Settings;
        if (settings.ShowLyrics) Hub.Lyrics.Request(m.Title, m.Artist, "", m.Duration);

        var art = new SKRect(body.Left + 18, body.Top + 6, body.Left + 138, body.Top + 126);
        DrawArtwork(ui, m, art, 16);

        var x0 = art.Right + 18;
        var x1 = body.Right - 22;
        var width = x1 - x0;
        // The source: click to keep showing this app's music, again to go back to any app's.
        var pinned = Hub.Settings.MusicSource.Length > 0;
        var source = pinned ? $"{m.SourceApp} · only this app" : m.SourceApp;
        ui.Text(source, x0, body.Top + 16, width - 30, 11.5f, Theme.Weight.Regular, pinned ? Theme.Accent : Theme.Dim);
        ui.Hit(new SKRect(x0 - 4, body.Top + 2, x0 + Math.Min(width - 30, ui.Measure(source, 11.5f)) + 4, body.Top + 22), () => CycleSource(m));
        visualizer.Draw(ui.Canvas, x1 - 18, body.Top + 11, 12, m.IsPlaying ? Theme.Accent : Theme.Faint, settings.Visualizer == "pixels");
        ui.Text(m.Title, x0, body.Top + 38, width, 17, Theme.Weight.Bold);
        ui.Text(m.Artist, x0, body.Top + 56, width, 13, Theme.Weight.Regular, Theme.Dim);

        // Progress, clickable to seek when the player allows it.
        var bar = new SKRect(x0, body.Top + 68, x1, body.Top + 72);
        var duration = m.Duration;
        var position = m.LivePosition;
        var fraction = duration > TimeSpan.Zero ? (float)Math.Clamp(position / duration, 0, 1) : 0;
        DrawProgress(ui, bar, fraction, m.IsPlaying, settings.ProgressBar);
        if (duration > TimeSpan.Zero)
        {
            ui.Text(Ui.Clock(position), x0, body.Top + 86, 60, 10.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text("-" + Ui.Clock(duration - position), x1, body.Top + 86, 60, 10.5f, Theme.Weight.Regular, Theme.Dim, SKTextAlign.Right);
            if (m.CanSeek)
                ui.Hit(new SKRect(bar.Left, bar.Top - 8, bar.Right, bar.Bottom + 8), () =>
                {
                    if (ui.Cursor is { } p) Hub.Media.Seek(duration * Math.Clamp((p.X - bar.Left) / bar.Width, 0, 1));
                });
        }

        // The line being sung.
        if (settings.ShowLyrics)
        {
            var line = Hub.Lyrics.LineAt(position + TimeSpan.FromMilliseconds(300));
            if (line != null)
                ui.Text(line, (x0 + x1) / 2, body.Top + 104, width, 12.5f, Theme.Weight.SemiBold, Theme.Accent, SKTextAlign.Center);
        }

        var mid = (x0 + x1) / 2;
        var cy = body.Top + 127;
        ui.IconButton(mid - 46, cy, 16, Icons.Previous, Hub.Media.Previous, enabled: m.CanPrevious);
        ui.IconButton(mid, cy, 17, m.IsPlaying ? Icons.Pause : (c, x, y, s, col) => Icons.Play(c, x + 1.5f, y, s, col), Hub.Media.TogglePlayPause, filled: true);
        ui.IconButton(mid + 46, cy, 16, Icons.Next, Hub.Media.Next, enabled: m.CanNext);
    }

    /// <summary>
    /// The song's progress in one of five looks: a plain bar, a wave that ripples while playing,
    /// shimmering pixels, a comet with a glowing head, or a row of hearts.
    /// </summary>
    private void DrawProgress(Ui ui, SKRect bar, float f, bool playing, string style)
    {
        var c = ui.Canvas;
        var head = bar.Left + bar.Width * f;
        var t = (float)Theme.Time;
        switch (style)
        {
            case "wave":
            {
                ui.Fill(new SKRect(head, bar.MidY - 1.5f, bar.Right, bar.MidY + 1.5f), 1.5f, Theme.Faint);
                var amp = playing ? 2.6f : 0f;
                using var path = new SKPathBuilder();
                path.MoveTo(bar.Left, bar.MidY);
                for (var x = bar.Left; x <= head; x += 2)
                    path.LineTo(x, bar.MidY + MathF.Sin((x - bar.Left) * 0.32f - t * 6) * amp);
                using var built = path.Detach();
                using var stroke = new SKPaint { Color = Theme.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round };
                c.DrawPath(built, stroke);
                ui.Circle(head, bar.MidY, 4, Theme.Text);
                break;
            }
            case "pixels":
            {
                const float cell = 4, gap = 1.5f;
                var count = (int)(bar.Width / (cell + gap));
                using var paint = new SKPaint { IsAntialias = false };
                for (var i = 0; i < count; i++)
                {
                    var x = bar.Left + i * (cell + gap);
                    var lit = x < head;
                    // Lit cells near the head shimmer.
                    var shimmer = lit && playing ? 0.75f + 0.25f * MathF.Sin(t * 8 - i * 0.6f) : 1;
                    paint.Color = lit ? Theme.Accent.WithAlpha((byte)(255 * shimmer)) : Theme.Faint;
                    c.DrawRect(x, bar.MidY - cell / 2, cell, cell, paint);
                }
                break;
            }
            case "comet":
            {
                ui.Fill(new SKRect(bar.Left, bar.MidY - 1, bar.Right, bar.MidY + 1), 1, Theme.Faint);
                using var tail = SKShader.CreateLinearGradient(new SKPoint(bar.Left, 0), new SKPoint(head, 0),
                    [Theme.Accent.WithAlpha(0), Theme.Accent], SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = tail, IsAntialias = true };
                c.DrawRoundRect(new SKRect(bar.Left, bar.MidY - 2, head, bar.MidY + 2), 2, 2, paint);
                var pulse = playing ? 6 + 2 * MathF.Sin(t * 5) : 5;
                using var glow = SKShader.CreateRadialGradient(new SKPoint(head, bar.MidY), pulse * 1.8f, [Theme.Accent.WithAlpha(160), Theme.Accent.WithAlpha(0)], SKShaderTileMode.Clamp);
                paint.Shader = glow;
                c.DrawCircle(head, bar.MidY, pulse * 1.8f, paint);
                ui.Circle(head, bar.MidY, 3.2f, Theme.Text);
                break;
            }
            case "hearts":
            {
                const float step = 11;
                var count = (int)(bar.Width / step);
                for (var i = 0; i <= count; i++)
                {
                    var x = bar.Left + i * step;
                    var lit = x <= head;
                    var beat = lit && playing && Math.Abs(x - head) < step ? 1.25f : 1f;
                    Heart(c, x, bar.MidY, 4.2f * beat, lit ? Theme.Accent : Theme.Faint);
                }
                break;
            }
            default:
                ui.Bar(new SKRect(bar.Left, bar.MidY - 2, bar.Right, bar.MidY + 2), f, Theme.Text);
                break;
        }
    }

    private static void Heart(SKCanvas c, float cx, float cy, float r, SKColor color)
    {
        using var path = new SKPathBuilder();
        path.MoveTo(cx, cy + r);
        path.CubicTo(cx - r * 1.6f, cy - r * 0.1f, cx - r * 0.8f, cy - r * 1.4f, cx, cy - r * 0.45f);
        path.CubicTo(cx + r * 0.8f, cy - r * 1.4f, cx + r * 1.6f, cy - r * 0.1f, cx, cy + r);
        path.Close();
        using var built = path.Detach();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(built, paint);
    }

    internal void DrawArtwork(Ui ui, MediaSnapshot m, SKRect box, float radius)
    {
        if (m.Artwork != null)
        {
            ui.Image(m.Artwork, box, radius);
            return;
        }
        ui.Canvas.Save();
        ui.Canvas.ClipRoundRect(new SKRoundRect(box, radius), SKClipOperation.Intersect, true);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Bottom),
            [Theme.Accent, Theme.Accent.WithAlpha(90)], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        ui.Canvas.DrawRect(box, paint);
        var initial = m.Title[..1].ToUpperInvariant();
        ui.Text(initial, box.MidX, box.MidY + box.Height * 0.16f, box.Width, box.Height * 0.45f, Theme.Weight.Bold, SKColors.White, SKTextAlign.Center);
        ui.Canvas.Restore();
    }
}
