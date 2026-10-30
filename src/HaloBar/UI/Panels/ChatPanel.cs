using HaloBar.Services;
using SkiaSharp;

namespace HaloBar.UI.Panels;

/// <summary>A Twitch channel's or a YouTube stream's live chat, newest at the bottom.</summary>
internal sealed class ChatPanel : Panel
{
    private readonly ChatService _chat;
    private readonly TextField _channel;

    public ChatPanel(Hub hub, ChatService chat) : base(hub)
    {
        _chat = chat;
        _channel = new TextField("Twitch channel, or a YouTube live link or @handle", text => _chat.Join(text));
    }

    public override string Title => "Chat";
    public override void Icon(SKCanvas c, float cx, float cy, float size, SKColor color) => Icons.Bubble(c, cx, cy, size, color);
    public override bool Available => Hub.Settings.ChatPanel;
    public override double FrameInterval => 0.25;

    public override IEnumerable<(string Label, Action Click)> Actions =>
        _chat.Channel.Length == 0 ? [] : [("Channel", () => { _channel.Text = _chat.Channel; _chat.Join(""); })];

    public override void Draw(Ui ui, SKRect body)
    {
        if (_chat.Channel.Length == 0)
        {
            ui.Text("Read a Twitch or YouTube live chat", body.MidX, body.Top + 34, body.Width - 40, 14, Theme.Weight.SemiBold, Theme.Text, SKTextAlign.Center);
            ui.Field(_channel, new SKRect(body.MidX - 150, body.Top + 52, body.MidX + 150, body.Top + 80), 12.5f);
            ui.Text("No login needed to read. Press Enter to join.", body.MidX, body.Top + 104, body.Width - 40, 11, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        var messages = _chat.Messages;
        ui.Text(_chat.Status, body.Right - 22, body.Top + 10, 200, 10.5f, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Right);
        if (messages.Count == 0)
        {
            ui.Text("Waiting for messages…", body.MidX, body.MidY + 4, body.Width, 12, Theme.Weight.Regular, Theme.Faint, SKTextAlign.Center);
            return;
        }
        // Newest at the bottom, as many as fit.
        var y = body.Bottom - 14;
        foreach (var m in messages.Reverse())
        {
            if (y < body.Top + 14) break;
            var nameWidth = ui.Measure(m.Name, 12, Theme.Weight.SemiBold);
            ui.Text(m.Name, body.Left + 20, y, 160, 12, Theme.Weight.SemiBold, m.Color);
            ui.Text(m.Text, body.Left + 26 + Math.Min(160, nameWidth), y, body.Width - 60 - Math.Min(160, nameWidth), 12);
            y -= 19;
        }
    }
}
