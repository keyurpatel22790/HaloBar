using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using QRCoder;

namespace HaloBar.Services;

/// <summary>
/// "Send to phone": while the Phone panel shows its QR code, a small web page is served on this
/// PC's Wi-Fi address. Scanning the code opens it: the phone can download what's on the file shelf
/// and the last copied text, and upload photos or files back, which land on the shelf. A random
/// token in the address keeps other devices on the network out, and the server stops a couple of
/// minutes after the panel closes.
/// </summary>
internal sealed class PhoneShareService : IDisposable
{
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(2);
    private readonly ShelfService _shelf;
    private readonly Func<string?> _copiedText;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";
    private DateTime _wantedAt;

    public string? Url { get; private set; }
    public bool[,]? Code { get; private set; }
    public bool NetworkIsPublic { get; private set; }
    public string Status { get; private set; } = "";
    public string UploadFolder { get; } = Path.Combine(Platform.Native.KnownFolder(Platform.Native.FolderDownloads) ?? Path.GetTempPath(), "From phone");

    /// <summary>Paths of files a phone uploaded.</summary>
    public event Action<string[]>? Uploaded;
    public event Action? Changed;

    public PhoneShareService(ShelfService shelf, Func<string?> copiedText)
    {
        _shelf = shelf;
        _copiedText = copiedText;
    }

    /// <summary>Call while the QR code is on screen; starts the server if needed.</summary>
    public void Touch()
    {
        _wantedAt = DateTime.UtcNow;
        if (_listener == null) Start();
    }

    private void Start()
    {
        var address = LanAddress();
        if (address == null)
        {
            Status = "Not connected to a network";
            return;
        }
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _listener = new TcpListener(address, 0);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Status = "Couldn't open a port";
            Log.Write($"Phone share: {ex.Message}");
            _listener = null;
            return;
        }
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://{address}:{port}/s/{_token}/";
        Code = Matrix(Url);
        NetworkIsPublic = IsPublicNetwork();
        Status = NetworkIsPublic ? "This network is set to Public, so Windows may turn phones away" : "";
        Log.Write($"Phone share on {Url}" + (NetworkIsPublic ? " (network is Public)" : ""));
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        _ = StopWhenIdleAsync(_cts.Token);
        Changed?.Invoke();
    }

    private async Task StopWhenIdleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (DateTime.UtcNow - _wantedAt > IdleStop) { Stop(); return; }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        Url = null;
        Code = null;
        Changed?.Invoke();
    }

    private static bool[,] Matrix(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var m = new bool[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                m[x, y] = rows[y][x];
        return m;
    }

    /// <summary>A private IPv4 address on a real (not virtual) adapter that is up.</summary>
    private static IPAddress? LanAddress()
    {
        string[] virtualNames = ["vEthernet", "VirtualBox", "VMware", "Hyper-V", "WSL", "Loopback", "Bluetooth", "Tailscale", "ZeroTier"];
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
                     .Where(n => !virtualNames.Any(v => n.Name.Contains(v, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count))
        {
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                var b = a.Address.GetAddressBytes();
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)) return a.Address;
            }
        }
        return null;
    }

    /// <summary>Windows blocks inbound connections from phones on networks marked Public.</summary>
    private static bool IsPublicNetwork()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
            foreach (var mo in searcher.Get())
                using (mo)
                    if (Convert.ToInt32(mo["NetworkCategory"]) == 0) return true;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Phone share: network category unknown: {ex.Message}");
        }
        return false;
    }

    // ───────────────────────────── the web page ─────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (method, target, headers, body) = await MiniHttp.ReadAsync(stream, 512 * 1024 * 1024, timeout.Token);
            var path = target.Split('?')[0];
            var prefix = $"/s/{_token}/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                await MiniHttp.ReplyAsync(stream, 404, "text/plain", "Not here"u8.ToArray());
                return;
            }
            _wantedAt = DateTime.UtcNow;
            var rest = path[prefix.Length..];
            var files = _shelf.Files;
            if (rest.StartsWith("f/", StringComparison.Ordinal) && int.TryParse(rest[2..], out var i) && i >= 0 && i < files.Count && File.Exists(files[i].Path))
            {
                await MiniHttp.SendFileAsync(stream, files[i].Path);
                return;
            }
            if (rest == "upload" && method == "POST")
            {
                var saved = MiniHttp.SaveMultipart(headers, body, UploadFolder);
                if (saved.Length > 0) Uploaded?.Invoke(saved);
                await MiniHttp.ReplyAsync(stream, 303, "text/plain", [], ("Location", prefix + "?sent=" + saved.Length));
                return;
            }
            await MiniHttp.ReplyAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(files, target.Contains("sent="))));
        }
        catch (Exception ex) when (ex is IOException or FormatException or OperationCanceledException or UnauthorizedAccessException)
        {
            Log.Write($"Phone share request: {ex.Message}");
        }
    }

    private string Page(IReadOnlyList<ShelfFile> files, bool sent)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>")
          .Append("<title>HaloBar</title><style>body{font:16px system-ui,sans-serif;background:#0b0b0c;color:#f5f5f7;margin:0;padding:20px}")
          .Append("h1{font-size:20px}a.f{display:flex;justify-content:space-between;padding:14px;margin:8px 0;border-radius:14px;background:#1c1c1f;color:#f5f5f7;text-decoration:none}")
          .Append("small{color:#999}.box{padding:14px;border-radius:14px;background:#1c1c1f;margin:12px 0;white-space:pre-wrap;word-break:break-word}")
          .Append("button,input{font:inherit}button{background:#7c9cff;color:#0b0b0c;border:0;border-radius:12px;padding:12px 18px;margin-top:10px}</style></head><body>")
          .Append("<h1>From your PC</h1>");
        if (sent) sb.Append("<div class=box>Sent. It's on the PC's shelf.</div>");
        if (files.Count == 0) sb.Append("<p><small>Nothing on the shelf yet. Drop files on the island to share them here.</small></p>");
        for (var i = 0; i < files.Count; i++)
        {
            long size = 0;
            try { size = new FileInfo(files[i].Path).Length; } catch (IOException) { }
            sb.Append($"<a class=f href='f/{i}'><span>{HttpUtility.HtmlEncode(files[i].Name)}</span><small>{DownloadService.Size(size)}</small></a>");
        }
        if (_copiedText() is { Length: > 0 } text)
            sb.Append("<h1>Last copied</h1><div class=box>").Append(HttpUtility.HtmlEncode(text.Length > 4000 ? text[..4000] + "…" : text)).Append("</div>");
        sb.Append("<h1>Send to the PC</h1><form method=post action='upload' enctype='multipart/form-data'>")
          .Append("<input type=file name=f multiple><br><button>Send</button></form></body></html>");
        return sb.ToString();
    }

    public void Dispose() => Stop();
}

/// <summary>Just enough HTTP/1.1 for the phone page: one request per connection.</summary>
internal static class MiniHttp
{
    public static async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] Body)> ReadAsync(
        NetworkStream stream, long maxBody, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var length = 0;
        int headerEnd;
        while ((headerEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (length == buffer.Length) throw new FormatException("headers too large");
            var n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) throw new IOException("closed early");
            length += n;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length < 2) throw new FormatException("bad request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var contentLength = headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var v) ? v : 0;
        if (contentLength > maxBody) throw new FormatException("too large");
        var body = new byte[contentLength];
        var have = Math.Min(length - (headerEnd + 4), (int)contentLength);
        Array.Copy(buffer, headerEnd + 4, body, 0, have);
        while (have < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(have), ct);
            if (n == 0) break;
            have += n;
        }
        return (request[0].ToUpperInvariant(), request[1], headers, body);
    }

    public static async Task ReplyAsync(NetworkStream stream, int status, string type, byte[] body, params (string Name, string Value)[] extra)
    {
        var reason = status switch { 200 => "OK", 303 => "See Other", 404 => "Not Found", _ => "Error" };
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n");
        foreach (var (name, value) in extra) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(body);
    }

    public static async Task SendFileAsync(NetworkStream stream, string path)
    {
        await using var file = File.OpenRead(path);
        var name = Uri.EscapeDataString(Path.GetFileName(path));
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {file.Length}\r\n" +
                   $"Content-Disposition: attachment; filename*=UTF-8''{name}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await file.CopyToAsync(stream);
    }

    /// <summary>Saves each file part of a multipart/form-data body into <paramref name="folder"/>.</summary>
    public static string[] SaveMultipart(Dictionary<string, string> headers, byte[] body, string folder)
    {
        if (!headers.TryGetValue("Content-Type", out var type)) return [];
        var marker = type.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase));
        if (marker == null) return [];
        var boundary = Encoding.ASCII.GetBytes("--" + marker[9..].Trim('"'));
        var saved = new List<string>();
        Directory.CreateDirectory(folder);
        var span = body.AsSpan();
        var pos = span.IndexOf(boundary);
        while (pos >= 0)
        {
            var start = pos + boundary.Length + 2; // skip CRLF after the boundary
            if (start >= span.Length) break;
            var next = span[start..].IndexOf(boundary);
            if (next < 0) break;
            var part = span.Slice(start, next - 2); // drop the CRLF before the next boundary
            var split = part.IndexOf("\r\n\r\n"u8);
            if (split > 0)
            {
                var head = Encoding.UTF8.GetString(part[..split]);
                var name = System.Text.RegularExpressions.Regex.Match(head, "filename=\"([^\"]*)\"").Groups[1].Value;
                if (name.Length > 0)
                {
                    name = string.Concat(Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
                    var path = Unique(Path.Combine(folder, name.Length == 0 ? "upload" : name));
                    File.WriteAllBytes(path, part[(split + 4)..].ToArray());
                    saved.Add(path);
                }
            }
            pos = start + next;
        }
        return [.. saved];
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
