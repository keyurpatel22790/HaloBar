using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaloBar.Services;

/// <summary>
/// Talks to OBS Studio over obs-websocket (built into OBS 28+): shows when it records or streams
/// and starts or stops either. With "use OBS's own settings" the port and password are read from
/// OBS's websocket config on this PC, so there's nothing to type. Reconnects while OBS runs.
/// </summary>
internal sealed class ObsService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private int _requestId;

    public bool Connected { get; private set; }
    public bool Recording { get; private set; }
    public bool Streaming { get; private set; }
    public DateTime RecordingSince { get; private set; }
    public DateTime StreamingSince { get; private set; }
    public string Status { get; private set; } = "Off";
    public event Action? Changed;

    public ObsService(AppSettings settings) => _settings = settings;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!_settings.ObsEnabled)
            {
                Status = "Off";
                await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                continue;
            }
            if (!System.Diagnostics.Process.GetProcessesByName("obs64").Any() && !System.Diagnostics.Process.GetProcessesByName("obs32").Any())
            {
                SetStatus("Waiting for OBS…");
                await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
                continue;
            }
            try
            {
                await RunAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                if (_cts.IsCancellationRequested) return;
                Log.Write($"OBS connection lost: {ex.Message}");
            }
            Connected = Recording = Streaming = false;
            SetStatus("Waiting for OBS…");
            await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
        }
    }

    private (int Port, string? Password) Endpoint()
    {
        if (_settings.ObsAuto)
        {
            // OBS 28+ keeps obs-websocket's settings here.
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obs-studio", "plugin_config", "obs-websocket", "config.json");
            try
            {
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var r = doc.RootElement;
                    var port = r.TryGetProperty("server_port", out var p) ? p.GetInt32() : 4455;
                    var auth = r.TryGetProperty("auth_required", out var a) && a.GetBoolean();
                    var password = auth && r.TryGetProperty("server_password", out var pw) ? pw.GetString() : null;
                    return (port, password);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Write($"OBS settings unreadable: {ex.Message}");
            }
        }
        return (_settings.ObsPort, string.IsNullOrEmpty(_settings.ObsPassword) ? null : _settings.ObsPassword);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var (port, password) = Endpoint();
        using var socket = new ClientWebSocket();
        _socket = socket;
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct);

        var hello = await ReceiveAsync(socket, ct) ?? throw new InvalidOperationException("no Hello from OBS");
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 64 }; // Outputs
        if (hello["d"]?["authentication"] is JsonObject auth)
        {
            if (password == null) throw new InvalidOperationException("OBS asks for a password and none is set");
            identify["authentication"] = Authenticate(password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        await SendAsync(socket, new JsonObject { ["op"] = 1, ["d"] = identify }, ct);
        var identified = await ReceiveAsync(socket, ct);
        if (identified?["op"]?.GetValue<int>() != 2) throw new InvalidOperationException("OBS rejected the password");

        Connected = true;
        SetStatus("Connected");
        Log.Write("OBS connected");
        await RequestAsync("GetRecordStatus", ct);
        await RequestAsync("GetStreamStatus", ct);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveAsync(socket, ct);
            if (msg == null) break;
            Handle(msg);
        }
    }

    /// <summary>obs-websocket v5: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    private static string Authenticate(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private void Handle(JsonNode msg)
    {
        var op = msg["op"]?.GetValue<int>();
        var d = msg["d"];
        if (op == 5) // event
        {
            var type = d?["eventType"]?.GetValue<string>();
            var active = d?["eventData"]?["outputActive"]?.GetValue<bool>() ?? false;
            if (type == "RecordStateChanged") SetRecording(active);
            else if (type == "StreamStateChanged") SetStreaming(active);
        }
        else if (op == 7) // request response
        {
            var type = d?["requestType"]?.GetValue<string>();
            var active = d?["responseData"]?["outputActive"]?.GetValue<bool>() ?? false;
            if (type == "GetRecordStatus") SetRecording(active);
            else if (type == "GetStreamStatus") SetStreaming(active);
        }
    }

    private void SetRecording(bool active)
    {
        if (active && !Recording) RecordingSince = DateTime.Now;
        Recording = active;
        Changed?.Invoke();
    }

    private void SetStreaming(bool active)
    {
        if (active && !Streaming) StreamingSince = DateTime.Now;
        Streaming = active;
        Changed?.Invoke();
    }

    public void ToggleRecord() => _ = RequestAsync("ToggleRecord", _cts.Token);
    public void ToggleStream() => _ = RequestAsync("ToggleStream", _cts.Token);

    private async Task RequestAsync(string type, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        try
        {
            await SendAsync(socket, new JsonObject
            {
                ["op"] = 6,
                ["d"] = new JsonObject { ["requestType"] = type, ["requestId"] = $"halobar-{Interlocked.Increment(ref _requestId)}" },
            }, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            Log.Write($"OBS request failed: {ex.Message}");
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonNode message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1 << 20) throw new InvalidOperationException("message too large for OBS");
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Abort();
    }
}
