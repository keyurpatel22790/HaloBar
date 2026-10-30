using System.Diagnostics;
using System.Net.NetworkInformation;

namespace HaloBar.Services;

/// <summary>
/// Whether the internet answers, how quickly (a ping every 20 seconds), and a download speed test
/// on request. Tells when the connection drops and when it comes back.
/// </summary>
internal sealed class NetworkHealth(Func<bool> enabled) : IDisposable
{
    private static readonly string[] Targets = ["1.1.1.1", "8.8.8.8"];
    private readonly CancellationTokenSource _stop = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Null until the first check.</summary>
    public bool? Online { get; private set; }
    public int? PingMs { get; private set; }
    public double? DownloadMbps { get; private set; }
    public bool Testing { get; private set; }
    public event Action? Changed;
    /// <summary>The connection dropped (false) or came back (true).</summary>
    public event Action<bool>? OnlineChanged;

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            if (enabled()) await Check();
            try { await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    });

    private async Task Check()
    {
        int? ping = null;
        foreach (var target in Targets)
        {
            try
            {
                using var p = new Ping();
                var reply = await p.SendPingAsync(target, 2000);
                if (reply.Status == IPStatus.Success)
                {
                    ping = (int)reply.RoundtripTime;
                    break;
                }
            }
            catch (PingException) { }
        }
        // Some networks drop pings: ask Windows' own connectivity page before calling it down.
        var online = ping != null || await Reachable();
        var was = Online;
        (Online, PingMs) = (online, ping);
        if (was != null && was != online) OnlineChanged?.Invoke(online);
        Changed?.Invoke();
    }

    private static async Task<bool> Reachable()
    {
        try
        {
            using var response = await Http.GetAsync("http://www.msftconnecttest.com/connecttest.txt");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Downloads up to 25 MB from Cloudflare's speed test (at most 10 seconds) and keeps the rate.</summary>
    public void TestSpeed()
    {
        if (Testing) return;
        Testing = true;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var clock = Stopwatch.StartNew();
                long bytes = 0;
                try
                {
                    using var response = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
                    var buffer = new byte[81920];
                    // The first moments include connecting; the clock starts with the first data.
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancel.Token)) > 0)
                    {
                        if (bytes == 0) clock.Restart();
                        bytes += read;
                    }
                }
                catch (OperationCanceledException) { } // ten seconds is enough to know
                DownloadMbps = bytes > 1_000_000 && clock.Elapsed.TotalSeconds > 0.2 ? bytes * 8 / clock.Elapsed.TotalSeconds / 1_000_000 : null;
            }
            catch (Exception ex)
            {
                DownloadMbps = null;
                Log.Write($"Speed test failed: {ex.Message}");
            }
            finally
            {
                Testing = false;
                Changed?.Invoke();
            }
        });
    }

    public void Dispose() => _stop.Cancel();
}
