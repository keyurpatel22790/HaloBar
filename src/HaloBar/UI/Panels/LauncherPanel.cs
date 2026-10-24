using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Six slots for favourite apps. An empty slot (or "Edit") opens a searchable list of every
/// Start-menu app; clicking one puts it in the slot.
/// </summary>
internal sealed class LauncherPanel : Panel
{
    private readonly LauncherService _launcher;
    private readonly TextField _search;
    private int? _picking;
    private bool _editing;
    private Spring _scroll = new(0, 300, 1);

    public LauncherPanel(Hub hub, LauncherService launcher) : base(hub)
    {
        _launcher = launcher;
        _search = new TextField("Search apps", _ => { });
    }

    public override string Title => "Apps";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Grid(c, cx, cy, size * 0.95f, color);
    public override bool Available => Hub.Settings.LauncherPanel;
    public override double FrameInterval => _picking != null ? 0.25 : 1;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        _picking != null ? [("Cancel", ClosePicker)] : [(_editing ? "Done" : "Edit", () => _editing = !_editing)];

    public override void Update(double dt) => _scroll.Step(dt);

    public override bool Wheel(float notches)
    {
        if (_picking == null) return false;
        _scroll.Target = Math.Max(0, _scroll.Target - notches * 60);
        return true;
    }

    private void ClosePicker()
    {
        _picking = null;
        _search.Clear();
        _scroll.Snap(0);
    }

    public override void Draw(Ui ui, SKRect body)
    {
        if (_picking is { } slot)
        {
            DrawPicker(ui, body, slot);
            return;
        }
        var slots = _launcher.Slotted;
        const float gap = 10;
        var w = (body.Width - 36 - gap * (LauncherService.Slots - 1)) / LauncherService.Slots;
        for (var i = 0; i < slots.Length; i++)
        {
            var x = body.Left + 18 + i * (w + gap);
            var r = new SKRect(x, body.Top + 8, x + w, body.Bottom - 16);
            var app = slots[i];
            var hovered = ui.Hovering(r);
            ui.Fill(r, 18, Theme.Overlay(hovered ? (byte)28 : (byte)14));
            var index = i;
            if (app == null)
            {
                Icons.Plus(ui.Canvas, r.MidX, r.MidY - 8, 22, Theme.Faint);
                ui.Text("Add app", r.MidX, r.Bottom - 16, w - 10, 11, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
                ui.Hit(r, () => { _picking = index; ui.Focus(_search); });
                continue;
            }
            if (_launcher.Icon(app) is { } icon) ui.Image(icon, Ui.Square(r.MidX, r.MidY - 10, 22), 6);
            else Icons.Grid(ui.Canvas, r.MidX, r.MidY - 10, 26, Theme.Dim);
            ui.Text(app.Name, r.MidX, r.Bottom - 16, w - 12, 11, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Center);
            if (_editing)
            {
                ui.IconButton(r.Right - 12, r.Top + 12, 9, Icons.Close, () => _launcher.Assign(index, null));
                ui.Hit(new SKRect(r.Left, r.Top + 24, r.Right, r.Bottom), () => { _picking = index; ui.Focus(_search); });
            }
            else ui.Hit(r, () => Hub.Open(app.ShellPath));
        }
    }

    private void DrawPicker(Ui ui, SKRect body, int slot)
    {
        ui.Field(_search, new SKRect(body.Left + 18, body.Top + 2, body.Right - 18, body.Top + 28), 12);
        var apps = _launcher.Apps;
        var area = new SKRect(body.Left + 12, body.Top + 34, body.Right - 12, body.Bottom - 6);
        if (apps == null)
        {
            ui.Text("Listing apps…", area.MidX, area.MidY, area.Width, 12, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        var query = _search.Text.Trim();
        var matches = (query.Length == 0 ? apps : apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))).Take(60).ToList();
        const float cellW = 87, cellH = 52;
        var perRow = (int)(area.Width / cellW);
        var rows = (matches.Count + perRow - 1) / perRow;
        _scroll.Target = Math.Clamp(_scroll.Target, 0, Math.Max(0, rows * cellH - area.Height));
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        for (var i = 0; i < matches.Count; i++)
        {
            var app = matches[i];
            var x = area.Left + 6 + i % perRow * cellW;
            var y = area.Top + 2 + i / perRow * cellH - (float)_scroll.Value;
            if (y + cellH < area.Top || y > area.Bottom) continue;
            var cell = new SKRect(x, y, x + cellW - 6, y + cellH - 4);
            if (ui.Hovering(cell) && ui.Hovering(area)) ui.Fill(cell, 10, Theme.Overlay(22));
            if (_launcher.Icon(app) is { } icon) ui.Image(icon, Ui.Square(cell.MidX, cell.Top + 17, 12), 3);
            else Icons.Grid(ui.Canvas, cell.MidX, cell.Top + 17, 16, Theme.Faint);
            ui.Text(app.Name, cell.MidX, cell.Bottom - 6, cell.Width - 6, 10, Theme.Weight.Regular, Theme.Text, SKTextAlign.Center);
            var chosen = app;
            ui.Hit(SKRect.Intersect(cell, area), () =>
            {
                _launcher.Assign(slot, chosen);
                ClosePicker();
            });
        }
        ui.Canvas.Restore();
        if (matches.Count == 0)
            ui.Text("No app called that", area.MidX, area.MidY, area.Width, 12, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
    }
}
