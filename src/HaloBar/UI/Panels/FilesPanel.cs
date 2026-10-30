using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// The file shelf: drop files onto the island to keep them at hand; screenshots land here too.
/// Click a file to open it; the small buttons show it in Explorer, copy it, or take it off the shelf.
/// </summary>
internal sealed class FilesPanel(Hub hub, Action<string[]> copyFiles) : Panel(hub)
{
    private const float CardWidth = 104, CardGap = 10;
    private Spring _scroll = new(0, 300, 1);

    /// <summary>Set while files are dragged over the island.</summary>
    public bool DropHover { get; set; }

    public override string Title => "Files";
    public override bool Available => Hub.Settings.ShelfEnabled;
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.File(c, cx, cy, size, color);
    public override double FrameInterval => _scroll.Settled && !DropHover ? 1 : 1 / 30.0;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        Hub.Shelf.Files.Count == 0 ? [] : [("Clear", () => { Hub.Shelf.Clear(); _scroll.Snap(0); })];

    public override void Update(double dt) => _scroll.Step(dt);

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, Hub.Shelf.Files.Count * (CardWidth + CardGap) - CardGap - 524);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 90, 0, max);
        return true;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        var files = Hub.Shelf.Files;
        var zone = new SKRect(body.Left + 14, body.Top + 2, body.Right - 14, body.Bottom - 10);
        if (DropHover)
        {
            // A dashed drop zone that breathes while files hover.
            var pulse = (byte)(40 + 25 * MathF.Sin((float)Theme.Time * 5));
            ui.Fill(zone, 16, Theme.Accent.WithAlpha(pulse));
            using var dash = new SKPaint
            {
                Color = Theme.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2,
                PathEffect = SKPathEffect.CreateDash([8, 6], (float)(Theme.Time * 20 % 14)),
            };
            ui.Canvas.DrawRoundRect(zone, 16, 16, dash);
            Icons.Download(ui.Canvas, zone.MidX, zone.MidY - 12, 26, Theme.Accent);
            ui.Text("Drop to keep them here", zone.MidX, zone.MidY + 22, zone.Width, 14, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Center);
            return;
        }
        if (files.Count == 0)
        {
            ui.Empty(body, "Drop files on the island", Hub.Hotkeys.Text("screenshot") is { Length: > 0 } keys ? $"They wait here until you need them. Screenshots ({keys}) land here too." : "They wait here until you need them. Screenshots land here too.");
            return;
        }

        var area = new SKRect(body.Left + 12, body.Top, body.Right - 12, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var x = body.Left + 18 - (float)_scroll.Value;
        foreach (var file in files)
        {
            var card = new SKRect(x, body.Top + 4, x + CardWidth, body.Bottom - 10);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;
            var hovered = ui.Hovering(card) && ui.Hovering(area);
            ui.Fill(card, 14, Theme.Overlay(hovered ? (byte)30 : (byte)16));

            var thumbBox = new SKRect(card.Left + 8, card.Top + 8, card.Right - 8, card.Bottom - 34);
            if (Hub.Shelf.Thumbnail(file.Path) is { } thumb)
            {
                // Photos fill the box; icons sit centred at their own size.
                var isIcon = thumb.Width <= 64;
                if (isIcon) ui.Image(thumb, Ui.Square(thumbBox.MidX, thumbBox.MidY, 24), 0);
                else ui.Image(thumb, thumbBox, 10);
            }
            else Icons.File(ui.Canvas, thumbBox.MidX, thumbBox.MidY, 30, Theme.Dim);

            ui.Text(file.Name, card.Left + 10, card.Bottom - 20, card.Width - 20, 11, Theme.Weight.SemiBold);
            ui.Text(Ui.Ago(file.Added), card.Left + 10, card.Bottom - 7, card.Width - 20, 9.5f, Theme.Weight.Regular, Theme.Faint);

            var chosen = file;
            ui.Hit(SKRect.Intersect(card, area), () => Hub.Open(chosen.Path));
            ui.DragFiles(SKRect.Intersect(card, area), () => [chosen.Path]);
            if (hovered)
            {
                // Small actions over the top of the card.
                var bx = card.Right - 14;
                ui.IconButton(bx, card.Top + 14, 10, Icons.Close, () => Hub.Shelf.Remove(chosen));
                ui.IconButton(bx - 22, card.Top + 14, 10, Icons.Clipboard, () =>
                {
                    copyFiles([chosen.Path]);
                    Hub.ShowHud(new Hud(HudIcon.Copied, chosen.Name, Detail: "Copied"));
                });
                ui.IconButton(bx - 44, card.Top + 14, 10, Icons.Folder, () => Hub.Reveal(chosen.Path));
            }
        }
        ui.Canvas.Restore();
    }
}
