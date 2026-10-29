using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>
/// What your AI coding agents are doing: working, waiting for you, done. "Connect" lists Claude
/// Code, Codex and Gemini CLI, each with its own switch.
/// </summary>
internal sealed class AgentsPanel(Hub hub, AgentService agents) : Panel(hub)
{
    private static readonly SKColor Waiting = new(0xFF, 0x9F, 0x0A), Done = new(0x34, 0xC7, 0x59), Error = new(0xFF, 0x45, 0x3A);
    private string _message = "";
    private bool _connecting;

    public override string Title => "Agents";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Spark(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.AgentsEnabled;
    public override int Badge => agents.Sessions.Count(s => s.State == AgentState.Waiting);
    public override double FrameInterval => agents.Sessions.Any(s => s.State == AgentState.Working) ? 1 / 20.0 : 1;

    public override IEnumerable<(string Label, Action Click)> Actions => [(Connections ? "Done" : "Connect", () =>
    {
        _connecting = !Connections;
        _message = "";
    })];

    /// <summary>The connections list shows when asked for, or while nothing is connected and nothing has reported.</summary>
    private bool Connections => _connecting || (!agents.AnyConnected && agents.Sessions.Count == 0);

    public override void GoTo(string place) => _connecting = place.Equals("connect", StringComparison.OrdinalIgnoreCase);

    public override void Draw(Ui ui, SKRect body)
    {
        if (Connections)
        {
            DrawConnections(ui, body);
            return;
        }
        var sessions = agents.Sessions;
        if (sessions.Count == 0)
        {
            var names = string.Join(", ", AgentService.Targets.Where(AgentService.Connected).Select(t => t.Name));
            ui.Empty(body, _message.Length > 0 ? _message : "No agents yet", $"Connected: {names}. Start or continue a session and it shows up here.");
            return;
        }
        var y = body.Top + 18;
        foreach (var s in sessions.Take(4))
        {
            var row = new SKRect(body.Left + 14, y - 16, body.Right - 14, y + 16);
            ui.Fill(row, 12, Theme.Overlay(12));
            var (color, label) = s.State switch
            {
                AgentState.Working => (Theme.Accent, "Working"),
                AgentState.Waiting => (Waiting, "Needs you"),
                AgentState.Error => (Error, "Error"),
                _ => (Done, "Done"),
            };
            // A spinner while working, a dot otherwise.
            if (s.State == AgentState.Working)
            {
                var a = (float)(Theme.Time * 360 % 360);
                using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, StrokeCap = SKStrokeCap.Round };
                ui.Canvas.DrawArc(Ui.Square(row.Left + 18, y, 7), a, 270, false, p);
            }
            else ui.Circle(row.Left + 18, y, 5, color);

            var title = s.Project.Length > 0 ? $"{s.Agent} · {s.Project}" : s.Agent;
            ui.Text(title, row.Left + 34, y - 2, 200, 12.5f, Theme.Weight.SemiBold);
            ui.Text(s.Doing, row.Left + 34, y + 11, row.Width - 170, 10.5f, Theme.Weight.Regular, Theme.Dim);
            ui.Text(label, row.Right - 64, y + 4, 90, 11, Theme.Weight.SemiBold, color, SKTextAlign.Right);
            ui.Text(Ui.Clock(DateTime.Now - s.Since), row.Right - 10, y + 4, 52, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Right);
            y += 37;
        }
    }

    /// <summary>One row per agent: connected or not, and a switch to change it.</summary>
    private void DrawConnections(Ui ui, SKRect body)
    {
        var y = body.Top + 18;
        foreach (var t in AgentService.Targets)
        {
            var row = new SKRect(body.Left + 14, y - 16, body.Right - 14, y + 16);
            ui.Fill(row, 12, Theme.Overlay(12));
            var installed = AgentService.Installed(t);
            var connected = AgentService.Connected(t);
            ui.Circle(row.Left + 18, y, 5, connected ? Done : installed ? Theme.Faint : Theme.Overlay(30));
            ui.Text(t.Name, row.Left + 34, y - 2, 200, 12.5f, Theme.Weight.SemiBold, installed ? Theme.Text : Theme.Dim);
            var detail = connected ? t.Note : installed ? "Reports what it's doing once connected" : "Not set up on this PC";
            ui.Text(detail, row.Left + 34, y + 11, row.Width - 150, 10.5f, Theme.Weight.Regular, Theme.Dim);
            if (installed)
            {
                var chosen = t;
                var label = connected ? "Disconnect" : "Connect";
                ui.Chip(row.Right - 12 - ui.Measure(label, 11.5f) - 22, y, label,
                    () => _message = connected ? agents.Disconnect(chosen) : agents.Connect(chosen), on: connected, size: 11.5f, accent: !connected);
            }
            y += 37;
        }
        var note = _message.Length > 0 ? _message : $"Hooks post to 127.0.0.1:{Hub.ApiPort} only. Anything else can report with /agent?name=..&state=..";
        ui.Text(note, body.MidX, body.Bottom - 12, body.Width - 40, 10.5f, Theme.Weight.Regular, _message.Length > 0 ? Theme.Text : Theme.Faint, SKTextAlign.Center);
    }
}
