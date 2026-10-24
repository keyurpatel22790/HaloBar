using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>What your AI coding agents are doing: working, waiting for you, done.</summary>
internal sealed class AgentsPanel(Hub hub, AgentService agents) : Panel(hub)
{
    private static readonly SKColor Waiting = new(0xFF, 0x9F, 0x0A), Done = new(0x34, 0xC7, 0x59), Error = new(0xFF, 0x45, 0x3A);
    private string _message = "";

    public override string Title => "Agents";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Spark(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.AgentsEnabled;
    public override int Badge => agents.Sessions.Count(s => s.State == AgentState.Waiting);
    public override double FrameInterval => agents.Sessions.Any(s => s.State == AgentState.Working) ? 1 / 20.0 : 1;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        !agents.ClaudeInstalled ? []
        : agents.ClaudeConnected ? [("Disconnect Claude Code", () => _message = agents.DisconnectClaude())]
        : [("Connect Claude Code", () => _message = agents.ConnectClaude())];

    public override void Draw(Ui ui, SKRect body)
    {
        var sessions = agents.Sessions;
        if (sessions.Count == 0)
        {
            var hint = agents.ClaudeConnected
                ? "Connected. Start or continue a Claude Code session and it shows up here."
                : agents.ClaudeInstalled
                    ? "Press “Connect Claude Code” above: HaloBar adds hooks that report to this PC only."
                    : $"Agents can report with: curl \"http://127.0.0.1:{Hub.ApiPort}/agent?name=Bot&state=working&text=Building\"";
            ui.Empty(body, _message.Length > 0 ? _message : "No agents yet", hint);
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
}
