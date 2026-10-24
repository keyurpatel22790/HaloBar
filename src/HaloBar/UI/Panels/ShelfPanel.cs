using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>The clipboard shelf: recent copies as cards; click one to copy it again.</summary>
internal sealed class ShelfPanel(Hub hub) : Panel(hub)
{
    private const float CardWidth = 128, CardGap = 10;
    private Spring _scroll = new(0, 300, 1);
    private ClipEntry? _flash;
    private DateTime _flashUntil;
    private string _filter = "all";
    private string? _flashSnippet;

    public override string Title => "Clipboard";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Clipboard(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.ClipboardEnabled;
    public override double FrameInterval => _scroll.Settled ? 1 : 0;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        Hub.Clipboard.Entries.Count == 0 ? [] : [("Clear", () => { Hub.Clipboard.Clear(); _scroll.Snap(0); })];

    private IEnumerable<ClipEntry> Shown => _filter switch
    {
        "text" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Text),
        "files" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Files),
        "pictures" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Image),
        _ => Hub.Clipboard.Entries,
    };

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, Shown.Count() * (CardWidth + CardGap) - CardGap - 524);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 90, 0, max);
        return true;
    }

    public override void Update(double dt) => _scroll.Step(dt);

    public override void Draw(Ui ui, SKRect body)
    {
        if (Hub.Clipboard.Entries.Count == 0 && Hub.Clipboard.Snippets.Count == 0)
        {
            ui.Empty(body, "Clipboard is empty", "Copy text, files or pictures and they'll wait here");
            return;
        }

        // Filters.
        var x = body.Left + 18;
        foreach (var (id, label) in new[] { ("all", "All"), ("text", "Text"), ("files", "Files"), ("pictures", "Pictures"), ("pinned", "Pinned") })
        {
            var chosen = id;
            x = ui.Chip(x, body.Top + 4, label, () => { _filter = chosen; _scroll.Snap(0); }, _filter == id, 11).Right + 5;
        }

        var top = body.Top + 22;
        if (_filter == "pinned")
        {
            DrawSnippets(ui, body, top);
            return;
        }
        var entries = Shown.ToList();
        if (entries.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), "Nothing of this kind yet", "");
            return;
        }
        var area = new SKRect(body.Left + 12, top, body.Right - 12, body.Bottom - 6);
        var c = ui.Canvas;
        c.Save();
        c.ClipRect(area);
        x = body.Left + 18 - (float)_scroll.Value;
        foreach (var entry in entries)
        {
            var card = new SKRect(x, top + 4, x + CardWidth, body.Bottom - 8);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;

            var hovered = ui.Hovering(card) && ui.Hovering(area);
            ui.Fill(card, 14, Theme.Overlay(hovered ? (byte)34 : (byte)18));
            var inner = Ui.Inflate(card, -10);
            switch (entry.Kind)
            {
                case ClipKind.Image when entry.Content.Image != null:
                    ui.Image(entry.Content.Image, new SKRect(card.Left + 6, card.Top + 6, card.Right - 6, card.Bottom - 24), 10);
                    break;
                case ClipKind.Files:
                    Icons.File(c, inner.Left + 9, inner.Top + 9, 18, Theme.Dim);
                    ui.Wrapped(entry.Preview, inner.Left, inner.Top + 20, inner.Width, 3, 11.5f);
                    break;
                default:
                    ui.Wrapped(entry.Preview, inner.Left, inner.Top - 3, inner.Width, 5, 11.5f);
                    break;
            }
            var label = entry.Kind switch { ClipKind.Image => "Picture", ClipKind.Files => "Files", _ => "Text" } + " · " + Ui.Ago(entry.Time);
            ui.Text(label, inner.Left, card.Bottom - 8, inner.Width, 10.5f, Theme.Weight.Regular, Theme.Faint);

            if (_flash == entry && DateTime.Now < _flashUntil)
            {
                ui.Fill(card, 14, Theme.Accent.WithAlpha(230));
                ui.Text("Copied", card.MidX, card.MidY + 5, card.Width, 13, Theme.Weight.Bold, Theme.OnAccent, SKTextAlign.Center);
            }

            var chosen = entry;
            ui.Hit(SKRect.Intersect(card, area), () =>
            {
                if (!Hub.Clipboard.Restore(chosen)) return;
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
                _scroll.Target = 0;
            });
            // Text can be pinned as a snippet that stays when the history moves on.
            if (hovered && entry.Kind == ClipKind.Text)
            {
                var pinned = Hub.Clipboard.IsPinned(entry.Content.Text!);
                ui.IconButton(card.Right - 13, card.Bottom - 13, 9, (cv, px, py, s, col) => Icons.Pin(cv, px, py, s, pinned ? Theme.Accent : col),
                    () => Hub.Clipboard.TogglePin(chosen.Content.Text!));
            }
        }
        c.Restore();
    }

    private void DrawSnippets(Ui ui, SKRect body, float top)
    {
        var snippets = Hub.Clipboard.Snippets;
        if (snippets.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), "No pinned snippets", "Hover a text card and press the pin to keep it here");
            return;
        }
        var area = new SKRect(body.Left + 12, top, body.Right - 12, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var x = body.Left + 18 - (float)_scroll.Value;
        foreach (var text in snippets)
        {
            var card = new SKRect(x, top + 4, x + CardWidth, body.Bottom - 8);
            x += CardWidth + CardGap;
            if (card.Right < area.Left || card.Left > area.Right) continue;
            var hovered = ui.Hovering(card) && ui.Hovering(area);
            ui.Fill(card, 14, Theme.Overlay(hovered ? (byte)34 : (byte)18));
            ui.Wrapped(text, card.Left + 10, card.Top + 7, card.Width - 20, 5, 11.5f);
            ui.Text("Pinned", card.Left + 10, card.Bottom - 8, card.Width - 20, 10.5f, Theme.Weight.Regular, Theme.Accent);
            if (_flashSnippet == text && DateTime.Now < _flashUntil)
            {
                ui.Fill(card, 14, Theme.Accent.WithAlpha(230));
                ui.Text("Copied", card.MidX, card.MidY + 5, card.Width, 13, Theme.Weight.Bold, Theme.OnAccent, SKTextAlign.Center);
            }
            var chosen = text;
            ui.Hit(SKRect.Intersect(card, area), () =>
            {
                if (!Hub.Clipboard.CopyText(chosen)) return;
                _flashSnippet = chosen;
                _flashUntil = DateTime.Now.AddSeconds(1.2);
            });
            if (hovered) ui.IconButton(card.Right - 13, card.Bottom - 13, 9, Icons.Close, () => Hub.Clipboard.TogglePin(chosen));
        }
        ui.Canvas.Restore();
    }
}
