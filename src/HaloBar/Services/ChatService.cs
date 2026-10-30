using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace HaloBar.Services;

internal sealed record ChatMessage(string Name, SKColor Color, string Text, DateTime Time);

/// <summary>
/// A live chat: a Twitch channel's, read anonymously over Twitch's IRC websocket (names keep the
/// colour each person picked), or a YouTube live stream's, read from the stream's public chat
/// page and then YouTube's own chat endpoint, as the chat pop-out does. No login or key for either.
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

    /// <summary>A YouTube link, a video link or an @handle; anything else is a Twitch channel.</summary>
    public static bool IsYouTube(string text) =>
        text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith('@');

    public void Join(string channel)
    {
        channel = channel.Trim();
        var youtube = IsYouTube(channel);
        if (!youtube)
        {
            channel = channel.TrimStart('#').ToLowerInvariant();
            if (channel.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) channel = channel.TrimEnd('/').Split('/')[^1];
        }
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
        _ = Task.Run(() => youtube ? RunYouTubeAsync(channel, ct) : RunAsync(channel, ct));
    }

    // ───────────────────────────── YouTube ─────────────────────────────

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI; CONSENT=YES+1"); // skip the EU consent page
        return http;
    }

    /// <summary>The video id in a watch, youtu.be, /live/ or /shorts/ link; null for a channel.</summary>
    internal static string? VideoId(string link)
    {
        var m = Regex.Match(link, @"(?:[?&]v=|youtu\.be/|/live/|/shorts/|/embed/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private async Task RunYouTubeAsync(string link, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Status = "Finding the stream…";
                Changed?.Invoke();
                var id = VideoId(link) ?? await LiveVideoOfChannelAsync(link, ct);
                if (id == null)
                {
                    Status = "Not live right now";
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }
                var page = await Http.GetStringAsync($"https://www.youtube.com/live_chat?is_popout=1&v={id}", ct);
                var token = Regex.Match(page, "\"continuation\":\"([^\"]+)\"").Groups[1].Value;
                var version = Regex.Match(page, "\"INNERTUBE_CONTEXT_CLIENT_VERSION\":\"([^\"]+)\"").Groups[1].Value;
                if (token.Length == 0)
                {
                    Status = "This stream has no live chat";
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }
                if (version.Length == 0) version = "2.20260101.00.00";
                Status = "YouTube live chat";
                Changed?.Invoke();
                while (!ct.IsCancellationRequested && token.Length > 0)
                {
                    var body = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["context"] = new Dictionary<string, object> { ["client"] = new Dictionary<string, string> { ["clientName"] = "WEB", ["clientVersion"] = version } },
                        ["continuation"] = token,
                    });
                    using var response = await Http.PostAsync("https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?prettyPrint=false",
                        new StringContent(body, Encoding.UTF8, "application/json"), ct);
                    response.EnsureSuccessStatusCode();
                    var (messages, next, wait) = ParseYouTube(await response.Content.ReadAsStringAsync(ct));
                    foreach (var m in messages) Add(m);
                    token = next ?? "";
                    await Task.Delay(Math.Clamp(wait, 1000, 10000), ct);
                }
                Status = "The stream ended";
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                Log.Write($"Chat (YouTube): {ex.Message}");
                Status = "Reconnecting…";
                Changed?.Invoke();
            }
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The live video of a channel (@handle, /channel/…, /c/…), from its /live page.</summary>
    internal static async Task<string?> LiveVideoOfChannelAsync(string link, CancellationToken ct)
    {
        var channel = link.StartsWith('@') ? $"https://www.youtube.com/{link}" : link.TrimEnd('/');
        if (!channel.EndsWith("/live", StringComparison.OrdinalIgnoreCase)) channel += "/live";
        var page = await Http.GetStringAsync(channel, ct);
        var canonical = Regex.Match(page, "<link rel=\"canonical\" href=\"([^\"]+)\"").Groups[1].Value;
        var id = VideoId(canonical);
        // A channel that isn't live points its /live page at the channel itself.
        return id != null && page.Contains("\"isLive\":true", StringComparison.Ordinal) ? id : null;
    }

    /// <summary>Messages, the next continuation and how long to wait, from a get_live_chat answer.</summary>
    internal static (List<ChatMessage> Messages, string? Next, int WaitMs) ParseYouTube(string json)
    {
        var messages = new List<ChatMessage>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("continuationContents", out var contents)
            || !contents.TryGetProperty("liveChatContinuation", out var chat)) return (messages, null, 0);
        if (chat.TryGetProperty("actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                if (!action.TryGetProperty("addChatItemAction", out var add) || !add.TryGetProperty("item", out var item)) continue;
                if (!item.TryGetProperty("liveChatTextMessageRenderer", out var r)
                    && !item.TryGetProperty("liveChatPaidMessageRenderer", out r)) continue;
                var name = r.TryGetProperty("authorName", out var an) && an.TryGetProperty("simpleText", out var st) ? st.GetString() ?? "" : "";
                var text = new StringBuilder();
                if (r.TryGetProperty("message", out var message) && message.TryGetProperty("runs", out var runs))
                    foreach (var run in runs.EnumerateArray())
                    {
                        if (run.TryGetProperty("text", out var t)) text.Append(t.GetString());
                        else if (run.TryGetProperty("emoji", out var emoji))
                            text.Append(emoji.TryGetProperty("emojiId", out var e) && e.GetString() is { Length: <= 4 } plain ? plain
                                : emoji.TryGetProperty("shortcuts", out var sc) && sc.GetArrayLength() > 0 ? sc[0].GetString() : "");
                    }
                if (r.TryGetProperty("purchaseAmountText", out var paid) && paid.TryGetProperty("simpleText", out var amount))
                    text.Insert(0, $"[{amount.GetString()}] ");
                if (name.Length > 0 && text.Length > 0) messages.Add(new ChatMessage(name.TrimStart('@'), NameColor(name), text.ToString(), DateTime.Now));
            }
        }
        string? next = null;
        var wait = 5000;
        if (chat.TryGetProperty("continuations", out var conts) && conts.GetArrayLength() > 0)
            foreach (var kind in conts[0].EnumerateObject())
            {
                if (kind.Value.TryGetProperty("continuation", out var c)) next = c.GetString();
                if (kind.Value.TryGetProperty("timeoutMs", out var w)) wait = w.GetInt32();
            }
        return (messages, next, wait);
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
