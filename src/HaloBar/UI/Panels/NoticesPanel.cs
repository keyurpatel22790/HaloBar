using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// Recent notifications, newest first. Clicking one opens it up to read the whole message (with a
/// button to open its app); a one-time code gets a Copy button.
/// </summary>
internal sealed class NoticesPanel(Hub hub) : Panel(hub)
{
    private const float RowHeight = 46, LineHeight = 15.6f, BodySize = 12;
    private const int MaxLines = 20;
    private Spring _scroll = new(0, 300, 1);
    // Notices opened up to read in full.
    private readonly HashSet<Notice> _open = new(ReferenceEqualityComparer.Instance);
    private float _contentHeight;

    public int Unseen { get; set; }

    public override string Title => "Notifications";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Bell(c, cx, cy, size, color);
    public override int Badge => Unseen;
    public override bool Available => Hub.Settings.NotificationsPanel;
    public override double FrameInterval => _scroll.Settled ? 1 : 0;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        Hub.Notices.Items.Count == 0 ? [] : [("Clear", () => { Hub.Notices.Clear(); _open.Clear(); _scroll.Snap(0); })];

    public override void Opened() => Unseen = 0;

    public override bool Wheel(float notches)
    {
        var max = Math.Max(0, _contentHeight - 140);
        _scroll.Target = Math.Clamp(_scroll.Target - notches * 46, 0, max);
        return true;
    }

    public override void Update(double dt) => _scroll.Step(dt);

    private static string Body(Notice n) => n.Body.ReplaceLineEndings(" ").Trim();

    private static float CodeWidth(Ui ui, Notice n) => n.Code is { } code ? ui.Measure($"Copy {code}", 11.5f) + 22 : 0;

    /// <summary>A row's height: one line folded; opened, the whole message and the Open button.</summary>
    private float Height(Ui ui, Notice n, float textWidth)
    {
        if (!_open.Contains(n)) return RowHeight;
        var lines = Body(n).Length == 0 ? 0 : ui.WrappedLines(Body(n), textWidth, MaxLines, BodySize);
        return 44 + lines * LineHeight + (n.AppId != null ? 30 : 4);
    }

    public override void Draw(Ui ui, SKRect body)
    {
        if (Hub.Notices.Items.Count == 0)
        {
            ui.Empty(body, "No notifications yet",
                $"Windows: {Hub.Notifications.Status}. Scripts: curl \"http://127.0.0.1:{Hub.ApiPort}/notify?title=Hi\"");
            return;
        }
        _open.IntersectWith(Hub.Notices.Items);
        var area = new SKRect(body.Left, body.Top, body.Right, body.Bottom - 6);
        ui.Canvas.Save();
        ui.Canvas.ClipRect(area);
        var clip = ui.HitClip;
        ui.HitClip = area;
        var y = body.Top + 2 - (float)_scroll.Value;
        var total = 0f;
        foreach (var n in Hub.Notices.Items)
        {
            var x = body.Left + 16 + 44;
            var codeWidth = CodeWidth(ui, n);
            var textRight = body.Right - 16 - (codeWidth > 0 ? codeWidth + 14 : 8);
            var fullWidth = body.Right - 16 - 8 - x;
            var height = Height(ui, n, fullWidth);
            var row = new SKRect(body.Left + 16, y, body.Right - 16, y + height - 4);
            var rowTop = total;
            y += height;
            total += height;
            if (row.Bottom < area.Top || row.Top > area.Bottom) continue;
            var open = _open.Contains(n);
            if (open) ui.Fill(row, 12, Theme.Overlay(14));
            else if (ui.Hovering(row)) ui.Fill(row, 12, Theme.Overlay(8));
            DrawIcon(ui, n, row.Left + 18, row.Top + 23, 16);
            ui.Text($"{n.App} · {Ui.Ago(n.Time)}", x, row.Top + 15, textRight - x, 10.5f, Theme.Weight.Regular, Theme.Dim);

            // Clicking a row opens it up to read in full (scrolled into view), or folds it again.
            var chosen = n;
            ui.Hit(row, () =>
            {
                if (_open.Remove(chosen)) return;
                _open.Add(chosen);
                _scroll.Target = Math.Max(0, rowTop - 2);
            });

            if (open)
            {
                ui.Text(n.Title, x, row.Top + 33, fullWidth, 13, Theme.Weight.SemiBold);
                if (Body(n).Length > 0) ui.Wrapped(Body(n), x, row.Top + 38, fullWidth, MaxLines, BodySize, Theme.Dim);
                if (n.AppId is { } app)
                    ui.Chip(x, row.Bottom - 15, $"Open {n.App}", () => Hub.Open($"shell:AppsFolder\\{app}"), size: 11);
            }
            else
            {
                using var title = Theme.Font(13, Theme.Weight.SemiBold, n.Title);
                var titleText = Theme.Fit(n.Title, (textRight - x) * 0.55f, title);
                ui.Text(titleText, x, row.Top + 33, textRight - x, 13, Theme.Weight.SemiBold);
                var tw = title.MeasureText(titleText) + 8;
                var room = textRight - x - tw;
                // A ⌄ says there's more to read.
                var more = ui.Measure(Body(n), BodySize) > room || titleText != n.Title;
                ui.Text(Body(n), x + tw, row.Top + 33, room - (more ? 14 : 0), BodySize, Theme.Weight.Regular, Theme.Dim);
                if (more) Icons.Arrow(ui.Canvas, textRight - 5, row.Top + 29, 8, Theme.Faint, up: false);
            }

            if (n.Code is { } code)
            {
                var copied = code;
                ui.Chip(row.Right - 8 - codeWidth, row.Top + 23, $"Copy {code}", () =>
                {
                    Hub.Clipboard.CopyText(copied);
                    Hub.ShowHud(new Hud(HudIcon.Copied, copied, Detail: "Code copied"));
                }, accent: true, size: 11.5f);
            }
        }
        _contentHeight = total;
        ui.HitClip = clip;
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
