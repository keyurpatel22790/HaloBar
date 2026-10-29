using HaloBar.Services;
using HaloBar.UI.Tiles;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Widget tiles in pages of four slots; a wide tile takes two. The wheel or the dots turn the
/// page. "Edit" picks the tiles: each click on a name adds it, then makes it wide (or narrow),
/// then takes it off.
/// </summary>
internal sealed class HomePanel : Panel
{
    private const int SlotsPerPage = 4;
    private readonly List<Tile> _catalogue;
    private bool _editing;
    private int _page;
    private Spring _slide = new(0, 260, 0.9);
    private float _editScroll, _editHeight, _editView;
    private readonly HashSet<string> _failed = [];

    public HomePanel(Hub hub) : base(hub) => _catalogue = TileCatalogue.Create(hub);

    public override string Title => "Home";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Home(c, cx, cy, size, color);

    public override IEnumerable<(string Label, Action Click)> Actions => [(_editing ? "Done" : "Edit", () =>
    {
        _editing = !_editing;
        _editScroll = 0;
    })];

    public override double FrameInterval
    {
        get
        {
            if (!_slide.Settled) return 1 / 60.0;
            var pages = Pages();
            if (_editing || pages.Count == 0) return 1;
            return pages[Math.Min(_page, pages.Count - 1)].Min(p => p.Tile.FrameInterval);
        }
    }

    private List<string> Chosen => Hub.Settings.HomeTiles;
    private List<string> Wide => Hub.Settings.HomeWide;

    /// <summary>The chosen tiles laid into pages: each entry is the tile, its first slot and how many it spans.</summary>
    private List<List<(Tile Tile, int Slot, int Span)>> Pages()
    {
        var pages = new List<List<(Tile, int, int)>>();
        var page = new List<(Tile, int, int)>();
        var slot = 0;
        foreach (var id in Chosen)
        {
            var tile = _catalogue.FirstOrDefault(t => t.Id == id);
            if (tile == null) continue;
            var span = Wide.Contains(id) ? 2 : 1;
            if (slot + span > SlotsPerPage)
            {
                pages.Add(page);
                page = [];
                slot = 0;
            }
            page.Add((tile, slot, span));
            slot += span;
        }
        if (page.Count > 0) pages.Add(page);
        return pages;
    }

    public override void Update(double dt) => _slide.Step(dt);

    /// <summary>"edit" opens the editor; a number turns to that page.</summary>
    public override void GoTo(string place)
    {
        _desktop = place.Equals("desktop", StringComparison.OrdinalIgnoreCase);
        _editing = _desktop || place.Equals("edit", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(place, out var page))
        {
            Turn(page - 1, Math.Max(1, Pages().Count));
            _slide.Snap(_page);
        }
    }

    public override bool Wheel(float notches)
    {
        if (_editing)
        {
            _editScroll = Math.Clamp(_editScroll - notches * 30, 0, Math.Max(0, _editHeight - _editView));
            return true;
        }
        var count = Pages().Count;
        if (count <= 1) return false;
        Turn(_page + (notches < 0 ? 1 : -1), count);
        return true;
    }

    private void Turn(int page, int count)
    {
        _page = Math.Clamp(page, 0, count - 1);
        _slide.Target = _page;
    }

    public override void Draw(Ui ui, SKRect body)
    {
        if (_editing)
        {
            DrawEditor(ui, body);
            return;
        }
        var pages = Pages();
        if (pages.Count == 0)
        {
            ui.Empty(body, "No tiles", "Choose some with Edit");
            return;
        }
        if (_page >= pages.Count) Turn(pages.Count - 1, pages.Count);

        const float gap = 10, side = 18;
        var dots = pages.Count > 1;
        var slotW = (body.Width - side * 2 - gap * (SlotsPerPage - 1)) / SlotsPerPage;
        var top = body.Top + 4;
        var bottom = body.Bottom - (dots ? 15 : 10);

        ui.Canvas.Save();
        ui.Canvas.ClipRect(new SKRect(body.Left + 4, body.Top, body.Right - 4, body.Bottom));
        for (var p = 0; p < pages.Count; p++)
        {
            var shift = (float)((p - _slide.Value) * body.Width);
            if (Math.Abs(shift) >= body.Width) continue;
            foreach (var (tile, slot, span) in pages[p])
            {
                var x = body.Left + side + shift + slot * (slotW + gap);
                var r = new SKRect(x, top, x + slotW * span + gap * (span - 1), bottom);
                DrawTile(ui, tile, r);
            }
        }
        ui.Canvas.Restore();

        if (!dots) return;
        var dotsY = body.Bottom - 6;
        var start = body.MidX - (pages.Count - 1) * 5;
        for (var p = 0; p < pages.Count; p++)
        {
            var cx = start + p * 10;
            ui.Circle(cx, dotsY, p == _page ? 3 : 2.3f, p == _page ? Theme.Text : Theme.Faint);
            var chosen = p;
            ui.Hit(new SKRect(cx - 5, dotsY - 6, cx + 5, dotsY + 6), () => Turn(chosen, pages.Count));
        }
    }

    private void DrawTile(Ui ui, Tile tile, SKRect r)
    {
        ui.Fill(r, 16, Theme.Overlay(14));
        ui.Canvas.Save();
        ui.Canvas.ClipRoundRect(new SKRoundRect(r, 16), SKClipOperation.Intersect, true);
        var saved = ui.Canvas.SaveCount;
        try
        {
            tile.Draw(ui, r);
            _failed.Remove(tile.Id);
        }
        catch (Exception ex)
        {
            // One broken tile shouldn't take the page down with it.
            if (_failed.Add(tile.Id)) Log.Write($"Tile {tile.Id} failed to draw: {ex}");
            ui.Canvas.RestoreToCount(saved);
            ui.Text($"{tile.Name} hit a problem", r.MidX, r.MidY, r.Width - 16, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
        }
        ui.Canvas.Restore();
    }

    // ───────────────────────────── editor ─────────────────────────────

    private void DrawEditor(Ui ui, SKRect body)
    {
        var view = new SKRect(body.Left, body.Top, body.Right, body.Bottom - 4);
        _editView = view.Height;
        ui.Canvas.Save();
        ui.Canvas.ClipRect(view);
        var y = body.Top + 14 - _editScroll;

        // Island or desktop: which the chips below place tiles on.
        var x = ui.Chip(body.Left + 18, y, "Island", () => _desktop = false, !_desktop, size: 11.5f).Right + 4;
        x = ui.Chip(x, y, "Desktop", () => _desktop = true, _desktop, size: 11.5f).Right + 12;
        ui.Text(_desktop ? "Click to pin, again for wide, again to unpin. Drag widgets; right-click one for more."
                : "Click to add, again to make it wide (or narrow), again to remove.", x, y + 4, body.Right - 20 - x, 11,
            Theme.Weight.Regular, Theme.Dim);
        y += 30;
        x = body.Left + 20;
        foreach (var tile in _catalogue)
        {
            string label;
            bool on;
            if (_desktop)
            {
                var place = Pinned.FirstOrDefault(p => p.Tile == tile.Id);
                on = place != null;
                label = place == null ? tile.Name : $"{tile.Name}{(place.Wide ? "  ▭▭" : "  ▭")}";
            }
            else
            {
                var index = Chosen.IndexOf(tile.Id);
                on = index >= 0;
                label = index < 0 ? tile.Name : $"{index + 1}  {tile.Name}{(Wide.Contains(tile.Id) ? "  ▭▭" : "")}";
            }
            var width = ui.Measure(label, 11.5f) + 22;
            if (x + width > body.Right - 20)
            {
                x = body.Left + 20;
                y += 28;
            }
            var chosen = tile;
            x = ui.Chip(x, y, label, () => { if (_desktop) Pin(chosen); else Cycle(chosen); }, on, size: 11.5f).Right + 6;
        }

        y += 36;
        ui.Text("Weather", body.Left + 20, y + 4, 80, 12, Theme.Weight.SemiBold);
        x = ui.Chip(body.Left + 90, y, Hub.Settings.WeatherFahrenheit ? "°F" : "°C", () =>
        {
            Hub.Settings.WeatherFahrenheit = !Hub.Settings.WeatherFahrenheit;
            Hub.Settings.Save();
            _ = Hub.Weather.RefreshAsync();
        }, true).Right + 6;
        x = ui.Chip(x, y, Hub.Settings.WeatherColourful ? "Colourful" : "Minimal", () =>
        {
            Hub.Settings.WeatherColourful = !Hub.Settings.WeatherColourful;
            Hub.Settings.Save();
        }, true).Right + 18;
        ui.Text("Focus", x, y + 4, 60, 12, Theme.Weight.SemiBold);
        ui.Chip(x + 46, y, $"{Hub.Settings.FocusMinutes} / {Hub.Settings.BreakMinutes} min", () =>
        {
            // Cycle through common Pomodoro lengths.
            (Hub.Settings.FocusMinutes, Hub.Settings.BreakMinutes) = (Hub.Settings.FocusMinutes, Hub.Settings.BreakMinutes) switch
            {
                (25, 5) => (50, 10),
                (50, 10) => (90, 15),
                (90, 15) => (15, 3),
                _ => (25, 5),
            };
            Hub.Settings.Save();
        }, true);

        y += 30;
        ui.Text("Breaks", body.Left + 20, y + 4, 80, 12, Theme.Weight.SemiBold);
        x = ui.Chip(body.Left + 90, y, Hub.Settings.BreakReminder ? "Remind me" : "No reminder", () =>
        {
            Hub.Settings.BreakReminder = !Hub.Settings.BreakReminder;
            Hub.Settings.Save();
        }, Hub.Settings.BreakReminder).Right + 6;
        if (Hub.Settings.BreakReminder)
            ui.Chip(x, y, $"every {Hub.Settings.BreakEveryMinutes} min", () =>
            {
                Hub.Settings.BreakEveryMinutes = Hub.Settings.BreakEveryMinutes switch { 30 => 45, 45 => 50, 50 => 60, 60 => 90, _ => 30 };
                Hub.Settings.Save();
            }, true);

        _editHeight = y + 20 + _editScroll - body.Top;
        ui.Canvas.Restore();

        // A thin scroll bar while the list is taller than the panel.
        if (_editHeight > _editView)
        {
            var track = view.Height - 8;
            var thumb = Math.Max(20, track * _editView / _editHeight);
            var at = view.Top + 4 + (track - thumb) * _editScroll / (_editHeight - _editView);
            ui.Fill(new SKRect(view.Right - 7, at, view.Right - 4, at + thumb), 1.5f, Theme.Faint);
        }
    }

    private bool _desktop;
    private List<DesktopWidgetPlace> Pinned => Hub.Settings.DesktopWidgets;

    /// <summary>On the desktop: not pinned → pinned at its usual width → the other width → not pinned.</summary>
    private void Pin(Tile tile)
    {
        var place = Pinned.FirstOrDefault(p => p.Tile == tile.Id);
        if (place == null) Pinned.Add(new DesktopWidgetPlace { Tile = tile.Id, Wide = tile.WideByDefault });
        else if (place.Wide == tile.WideByDefault)
        {
            // Same spot, other width: a new entry so the widget window is rebuilt at its new size.
            Pinned[Pinned.IndexOf(place)] = new DesktopWidgetPlace { Tile = place.Tile, X = place.X, Y = place.Y, Wide = !place.Wide };
        }
        else Pinned.Remove(place);
        Hub.Settings.Save();
    }

    /// <summary>Not shown → shown at its usual width → the other width → not shown.</summary>
    private void Cycle(Tile tile)
    {
        var id = tile.Id;
        if (!Chosen.Contains(id))
        {
            Chosen.Add(id);
            if (tile.WideByDefault) Wide.Add(id);
            else Wide.Remove(id);
        }
        else if (Wide.Contains(id) == tile.WideByDefault)
        {
            if (!Wide.Remove(id)) Wide.Add(id);
        }
        else
        {
            Chosen.Remove(id);
            Wide.Remove(id);
        }
        Hub.Settings.Save();
    }
}
