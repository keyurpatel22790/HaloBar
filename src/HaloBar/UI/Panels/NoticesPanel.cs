using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>Recent notifications, newest first.</summary>
internal sealed class NoticesPanel(Hub hub) : Panel(hub)
{
    private const float RowHeight = 46;
    private Spring _scroll = new(0, 300, 1);

    public int Unseen { get; set; }

    public override string Title => "Notifications";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Bell(c, cx, cy, size, color);
    public override int Badge => Unseen;
    public override bool Available => Hub.Settings.NotificationsPanel;
    public override double FrameInterval => _scroll.Settled ? 1 : 0;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        Hub.Notices.Items.Count == 0 ? [] : [("Clear", () => { Hub.Notices.Clear(); _scroll.Snap(0); })];

    public override void Opened() => Unseen = 0;

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, Hub.Notices.Items.Count * RowHeight - 140);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 46, 0, max);
        return true;
    }

    public override void Update(double dt) => _scroll.Step(dt);

    public override void Draw(Ui ui, SKRect body)
    {
        if (Hub.Notices.Items.Count == 0)
        {
            ui.Empty(body, "No notifications yet",
                $"Windows: {Hub.Notifications.Status}. Scripts: curl \"http://127.0.0.1:{Hub.ApiPort}/notify?title=Hi\"");
            return;
        }
        var area = new SKRect(body.Left, body.Top, body.Right, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var y = body.Top + 2 - (float)_scroll.Value;
        foreach (var n in Hub.Notices.Items)
        {
            var row = new SKRect(body.Left + 16, y, body.Right - 16, y + RowHeight - 4);
            y += RowHeight;
            if (row.Bottom < area.Top || row.Top > area.Bottom) continue;
            DrawIcon(ui, n, row.Left + 18, row.MidY, 16);
            var x = row.Left + 44;
            ui.Text($"{n.App} · {Ui.Ago(n.Time)}", x, row.Top + 15, row.Right - x, 10.5f, Theme.Weight.Regular, Theme.Dim);
            using var title = Theme.Font(13, Theme.Weight.SemiBold, n.Title);
            var titleText = Theme.Fit(n.Title, (row.Right - x) * 0.55f, title);
            ui.Text(titleText, x, row.Top + 33, row.Right - x, 13, Theme.Weight.SemiBold);
            var tw = title.MeasureText(titleText) + 8;
            ui.Text(n.Body, x + tw, row.Top + 33, row.Right - x - tw, 12, Theme.Weight.Regular, Theme.Dim);
            if (n.AppId is { } app) ui.Hit(row, () => Hub.Open($"shell:AppsFolder\\{app}"));
        }
        ui.Canvas.Restore();
    }

    public static void DrawIcon(Ui ui, Notice n, float cx, float cy, float radius)
    {
        var box = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        if (n.Icon != null)
        {
            ui.Image(n.Icon, box, radius * 0.45f);
            return;
        }
        ui.Fill(box, radius * 0.45f, ui.Theme.Accent);
        var initial = string.IsNullOrEmpty(n.App) ? "•" : n.App[..1].ToUpperInvariant();
        ui.Text(initial, cx, cy + radius * 0.32f, radius * 2, radius * 0.9f, Theme.Weight.Bold, ui.Theme.OnAccent, SKTextAlign.Center);
    }
}
