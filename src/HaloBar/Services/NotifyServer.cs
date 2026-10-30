using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace HaloBar.Services;

/// <summary>
/// A tiny HTTP endpoint on 127.0.0.1 so scripts, build tools and AI agents can put a notice on
/// the island:
///   GET  /notify?title=Build&amp;body=passed&amp;app=CI
///   POST /notify   {"title":"Build","body":"passed","app":"CI"}
/// and change the island's colours:
///   GET  /style?surface=sakura&amp;accent=%23FF8800&amp;album=on
/// It binds to loopback only, so nothing on the network can reach it. A raw TcpListener is used
/// instead of HttpListener because http.sys URL reservations need admin rights.
/// </summary>
internal sealed class NotifyServer : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public event Action<Notice>? Received;
    /// <summary>Surface id, accent id or "#RRGGBB", and album-card switch; null leaves that part as it is.</summary>
    public event Action<string?, string?, bool?>? StyleRequested;
    /// <summary>Open the island on a panel by name ("home", "music"…), or "none" to close it.</summary>
    public event Action<string>? PanelRequested;
    /// <summary>/settings?page=… opens the Settings window (on that page).</summary>
    public event Action<string?>? SettingsRequested;
    /// <summary>(agent id, the JSON its hook received) from Claude Code, Codex or Gemini CLI.</summary>
    public event Action<string, string>? HookEvent;
    /// <summary>(name, state, text, project) from any other agent or script.</summary>
    public event Action<string, string?, string?, string?>? AgentEvent;
    /// <summary>"start", "stop" or "toggle" for the screen recorder.</summary>
    public event Action<string>? RecordRequested;
    /// <summary>A pinned site's name or an address to show in the web drawer, or "none" to hide it.</summary>
    public event Action<string>? WebRequested;
    /// <summary>(PNG path, panel to open first or null).</summary>
    public event Action<string, string?>? SnapshotRequested;

    public NotifyServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            Log.Write($"Local API: listening on http://127.0.0.1:{Port}/notify");
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (SocketException ex)
        {
            Log.Write($"Local API: port {Port} unavailable: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        try
        {
            var (method, target, body) = await ReadRequestAsync(stream, timeout.Token);
            var uri = new Uri("http://127.0.0.1" + target);
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path == "/style")
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                bool? album = q["album"] switch { "on" or "true" => true, "off" or "false" => false, _ => null };
                if (q["surface"] == null && q["accent"] == null && album == null)
                {
                    await ReplyAsync(stream, 400, "use /style?surface=..&accent=..&album=on|off");
                    return;
                }
                StyleRequested?.Invoke(q["surface"], q["accent"], album);
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path == "/record")
            {
                var action = HttpUtility.ParseQueryString(uri.Query)["action"]?.ToLowerInvariant() ?? "toggle";
                if (action is not ("start" or "stop" or "toggle"))
                {
                    await ReplyAsync(stream, 400, "use /record?action=start|stop|toggle");
                    return;
                }
                RecordRequested?.Invoke(action);
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path == "/web")
            {
                var site = HttpUtility.ParseQueryString(uri.Query)["site"];
                if (string.IsNullOrWhiteSpace(site))
                {
                    await ReplyAsync(stream, 400, "use /web?site=<pinned name or address>, or /web?site=none to hide it");
                    return;
                }
                WebRequested?.Invoke(site);
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path == "/snapshot")
            {
                // Saves the next frames of the primary island to a PNG: for docs and checking
                // the look without screen capture (which can't see it when it's excluded).
                var q = HttpUtility.ParseQueryString(uri.Query);
                var file = Path.Combine(AppPaths.Folder, "snapshot.png");
                SnapshotRequested?.Invoke(file, q["panel"]);
                await ReplyAsync(stream, 200, file);
                return;
            }
            if (path is "/agent/claude" or "/agent/codex" or "/agent/gemini" && method == "POST")
            {
                HookEvent?.Invoke(path[7..], body);
                // Agents read a hook's output as JSON; an empty object means "carry on".
                await ReplyAsync(stream, 200, "{}");
                return;
            }
            if (path == "/agent")
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                if (string.IsNullOrWhiteSpace(q["name"]))
                {
                    await ReplyAsync(stream, 400, "use /agent?name=..&state=working|waiting|done|error&text=..&project=..");
                    return;
                }
                AgentEvent?.Invoke(Cut(q["name"], 40)!, q["state"], Cut(q["text"], 120), Cut(q["project"], 60));
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path == "/settings")
            {
                SettingsRequested?.Invoke(HttpUtility.ParseQueryString(uri.Query)["page"]);
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path == "/panel")
            {
                var name = HttpUtility.ParseQueryString(uri.Query)["name"];
                if (string.IsNullOrWhiteSpace(name))
                {
                    await ReplyAsync(stream, 400, "use /panel?name=home|music|clipboard|notifications|reminders|controls|style|settings|none");
                    return;
                }
                PanelRequested?.Invoke(name);
                await ReplyAsync(stream, 200, "ok");
                return;
            }
            if (path != "/notify")
            {
                await ReplyAsync(stream, 404, "use GET /notify?title=..&body=..&app=.., POST /notify with JSON {title, body, app}, or /style?surface=..&accent=..&album=on|off");
                return;
            }

            string? title, text, app;
            if (method == "POST" && body.Length > 0)
            {
                using var doc = JsonDocument.Parse(body);
                title = Prop(doc, "title");
                text = Prop(doc, "body");
                app = Prop(doc, "app");
            }
            else
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                (title, text, app) = (q["title"], q["body"], q["app"]);
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text))
            {
                await ReplyAsync(stream, 400, "title or body is required");
                return;
            }
            Received?.Invoke(new Notice(Cut(app, 40) ?? "Local API", Cut(title, 120) ?? "", Cut(text, 400) ?? "", null, DateTime.Now));
            await ReplyAsync(stream, 200, "ok");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UriFormatException or OperationCanceledException or FormatException)
        {
            try { await ReplyAsync(stream, 400, "bad request"); } catch { }
        }
    }

    private static string? Prop(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max] + "…";

    private static async Task<(string Method, string Target, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, length, "\r\n\r\n"u8)) < 0)
        {
            if (length == buffer.Length) throw new FormatException("request too large");
            var n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) throw new IOException("closed early");
            length += n;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length < 2) throw new FormatException("bad request line");

        var contentLength = 0;
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(colon + 1)..].Trim());
        }
        var bodyStart = headerEnd + 4;
        // Agent hook events can carry a whole file being written; allow bodies up to 4 MB.
        if (contentLength > MaxBodyBytes) throw new FormatException("body too large");
        if (bodyStart + contentLength > buffer.Length) Array.Resize(ref buffer, bodyStart + contentLength);
        while (length < bodyStart + contentLength)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) break;
            length += n;
        }
        var body = Encoding.UTF8.GetString(buffer, bodyStart, Math.Min(contentLength, length - bodyStart));
        return (requestLine[0].ToUpperInvariant(), requestLine[1], body);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);

    private static async Task ReplyAsync(NetworkStream stream, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text + "\n");
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
