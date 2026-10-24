using System.Net.WebSockets;
using System.Text;
using SkiaSharp;

namespace HaloBar.Services;

internal sealed record ChatMessage(string Name, SKColor Color, string Text, DateTime Time);

/// <summary>
/// A Twitch channel's live chat, read anonymously over Twitch's IRC websocket (no login needed to
/// read). Names keep the colour each person picked on Twitch.
/// </summary>
internal sealed class ChatService : IDisposable
{
    private const int Keep = 60;
    private readonly AppSettings _settings;
    private readonly LinkedList<ChatMessage> _messages = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public string Channel { get; private set; } = "";
    public string Status { get; private set; } = "";
    public event Action<ChatMessage>? Message;
    public event Action? Changed;

    public ChatService(AppSettings settings) => _settings = settings;

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    public void Start() => Join(_settings.ChatTwitch);

    public void Join(string channel)
    {
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        if (channel.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) channel = channel.TrimEnd('/').Split('/')[^1];
        _settings.ChatTwitch = channel;
        _settings.Save();
        _cts?.Cancel();
        lock (_gate) _messages.Clear();
        Channel = channel;
        if (channel.Length == 0)
        {
            Status = "";
            Changed?.Invoke();
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(channel, ct));
    }

    private async Task RunAsync(string channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                Status = $"Joining #{channel}…";
                Changed?.Invoke();
                await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), ct);
                foreach (var line in new[] { "CAP REQ :twitch.tv/tags", "PASS SCHMOOPIIE", $"NICK justinfan{Random.Shared.Next(10000, 99999)}", $"JOIN #{channel}" })
                    await SendAsync(ws, line, ct);
                Status = $"#{channel}";
                Changed?.Invoke();
                var buffer = new byte[16 * 1024];
                var pending = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    foreach (var line in pending.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("PING", StringComparison.Ordinal)) await SendAsync(ws, "PONG" + line[4..], ct);
                        else if (Parse(line) is { } message) Add(message);
                    }
                    pending.Clear();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                Log.Write($"Chat: {ex.Message}");
            }
            Status = "Reconnecting…";
            Changed?.Invoke();
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>"@tags :nick!nick@nick.tmi.twitch.tv PRIVMSG #chan :text" → a message.</summary>
    internal static ChatMessage? Parse(string line)
    {
        var tags = new Dictionary<string, string>();
        var rest = line;
        if (rest.StartsWith('@'))
        {
            var space = rest.IndexOf(' ');
            foreach (var tag in rest[1..space].Split(';'))
            {
                var eq = tag.IndexOf('=');
                if (eq > 0) tags[tag[..eq]] = tag[(eq + 1)..];
            }
            rest = rest[(space + 1)..];
        }
        var cmd = rest.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
        if (cmd < 0 || !rest.StartsWith(':')) return null;
        var nick = rest[1..rest.IndexOf('!')];
        var colon = rest.IndexOf(" :", cmd, StringComparison.Ordinal);
        if (colon < 0) return null;
        var text = rest[(colon + 2)..];
        if (text.StartsWith("\u0001ACTION ", StringComparison.Ordinal)) text = text[8..].TrimEnd('\u0001');
        var name = tags.TryGetValue("display-name", out var dn) && dn.Length > 0 ? dn : nick;
        var color = tags.TryGetValue("color", out var c) && SKColor.TryParse(c, out var parsed) ? parsed : NameColor(nick);
        return new ChatMessage(name, color, text, DateTime.Now);
    }

    /// <summary>People without a chosen colour get a steady one from their name.</summary>
    private static SKColor NameColor(string nick) => SKColor.FromHsv(Math.Abs(nick.GetHashCode()) % 360, 55, 95);

    private void Add(ChatMessage m)
    {
        lock (_gate)
        {
            _messages.AddLast(m);
            while (_messages.Count > Keep) _messages.RemoveFirst();
        }
        Message?.Invoke(m);
        Changed?.Invoke();
    }

    private static Task SendAsync(ClientWebSocket ws, string line, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, ct);

    public void Dispose() => _cts?.Cancel();
}
