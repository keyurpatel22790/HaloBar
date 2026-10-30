using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>The clipboard shelf: recent copies as cards; click one to copy it again. A search box narrows them down.</summary>
internal sealed class ShelfPanel : Panel
{
    private const float CardWidth = 128, CardGap = 10;
    private readonly TextField _search;

    public ShelfPanel(Hub hub) : base(hub)
    {
        // Enter copies the first match.
        _search = new TextField("Search", _ =>
        {
            if (_filter == "pinned")
            {
                if (Pinned.FirstOrDefault() is { } text) Hub.Clipboard.CopyText(text);
            }
            else if (Shown.FirstOrDefault() is { } first && Hub.Clipboard.Restore(first))
            {
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
            }
        });
    }

    /// <summary>The search box (the clipboard hotkey types into it).</summary>
    public TextField SearchField => _search;

    private bool Matches(string? text) => _search.Text.Length == 0 || text?.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;

    private bool Matches(ClipEntry e) => _search.Text.Length == 0 || e.Kind switch
    {
        ClipKind.Files => e.Content.Files?.Any(f => Matches(Path.GetFileName(f))) == true,
        ClipKind.Image => Matches("picture"),
        _ => Matches(e.Content.Text),
    };

    private IEnumerable<string> Pinned => Hub.Clipboard.Snippets.Where(Matches);
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

    private IEnumerable<ClipEntry> Shown => (_filter switch
    {
        "text" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Text),
        "files" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Files),
        "pictures" => Hub.Clipboard.Entries.Where(e => e.Kind == ClipKind.Image),
        _ => Hub.Clipboard.Entries,
    }).Where(Matches);

    public override bool Wheel(float notches)
    {
        var max = Hub.Settings.ClipboardList && _filter != "pinned"
            ? Math.Max(0, Shown.Count() * ListRow - 150)
            : Math.Max(0, Shown.Count() * (CardWidth + CardGap) - CardGap - 524);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 90, 0, max);
        return true;
    }

    public override void Update(double dt) => _scroll.Step(dt);

    public override void Draw(Ui ui, SKRect body)
    {
        if (Hub.Clipboard.Entries.Count == 0 && Hub.Clipboard.Snippets.Count == 0 && _search.Text.Length == 0)
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
        var search = new SKRect(Math.Max(x + 10, body.Right - 230), body.Top - 7, body.Right - 18, body.Top + 15);
        ui.Field(_search, search, 11.5f);
        if (_search.Text.Length > 0) ui.IconButton(search.Right - 12, search.MidY, 8, Icons.Close, () => _search.Clear());

        var top = body.Top + 22;
        if (_filter == "pinned")
        {
            DrawSnippets(ui, body, top);
            return;
        }
        var entries = Shown.ToList();
        if (entries.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), _search.Text.Length > 0 ? "No matches" : "Nothing of this kind yet",
                _search.Text.Length > 0 ? "Press the ✕ to see everything again" : "");
            return;
        }
        var area = new SKRect(body.Left + 12, top, body.Right - 12, body.Bottom - 6);
        if (Hub.Settings.ClipboardList)
        {
            DrawList(ui, area, entries);
            return;
        }
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

    private const float ListRow = 26;

    /// <summary>The history as one line per entry, newest first; the wheel scrolls down the list.</summary>
    private void DrawList(Ui ui, SKRect area, List<ClipEntry> entries)
    {
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = area.Top + 6 - (float)_scroll.Value;
        foreach (var entry in entries)
        {
            var row = new SKRect(area.Left, y, area.Right, y + ListRow - 2);
            y += ListRow;
            if (row.Bottom < area.Top || row.Top > area.Bottom) continue;
            var hovered = ui.Hovering(row) && ui.Hovering(area);
            var flashing = _flash == entry && DateTime.Now < _flashUntil;
            ui.Fill(row, 8, flashing ? Theme.Accent.WithAlpha(230) : Theme.Overlay(hovered ? (byte)30 : (byte)12));
            var kind = entry.Kind switch { ClipKind.Image => "Picture", ClipKind.Files => "Files", _ => "Text" };
            var line = flashing ? "Copied" : entry.Preview.ReplaceLineEndings(" ");
            ui.Text(line, row.Left + 10, row.MidY + 4, row.Width - 130, 11.5f, flashing ? Theme.Weight.Bold : Theme.Weight.Regular, flashing ? Theme.OnAccent : Theme.Text);
            if (!flashing) ui.Text($"{kind} · {Ui.Ago(entry.Time)}", row.Right - 34, row.MidY + 4, 90, 10, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Right);
            var chosen = entry;
            ui.Hit(SKRect.Intersect(row, area), () =>
            {
                if (!Hub.Clipboard.Restore(chosen)) return;
                _flash = Hub.Clipboard.Entries[0];
                _flashUntil = DateTime.Now.AddSeconds(1.2);
                _scroll.Target = 0;
            });
            if (hovered && entry.Kind == ClipKind.Text)
            {
                var pinned = Hub.Clipboard.IsPinned(entry.Content.Text!);
                ui.IconButton(row.Right - 14, row.MidY, 9, (cv, px, py, s, col) => Icons.Pin(cv, px, py, s, pinned ? Theme.Accent : col),
                    () => Hub.Clipboard.TogglePin(chosen.Content.Text!));
            }
        }
        ui.Canvas.Restore();
    }

    private void DrawSnippets(Ui ui, SKRect body, float top)
    {
        var snippets = Pinned.ToList();
        if (snippets.Count == 0)
        {
            ui.Empty(new SKRect(body.Left, top, body.Right, body.Bottom), _search.Text.Length > 0 ? "No matches" : "No pinned snippets",
                _search.Text.Length > 0 ? "" : "Hover a text card and press the pin to keep it here");
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
