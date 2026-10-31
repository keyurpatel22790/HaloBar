using System.Diagnostics;
using System.Text.Json;

namespace HaloBar.Services;

/// <summary>
/// Keeps an LG TV's Developer Mode from running out, so apps installed through it (ad-free
/// YouTube) stay. Developer Mode lasts 1000 hours and deletes those apps when it ends; this renews
/// it once a day the way webOS Dev Manager's renewal script does: it reads the TV's session token
/// over SSH (the restricted "prisoner" account, key from the TV's Key Server) and asks LG's
/// developer site to reset the timer. The token is kept, so a renewal works while the TV is off.
/// </summary>
internal sealed class TvDevModeService(AppSettings settings) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _stop = new();
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaloBar", "tv");
    private static string KeyFile => Path.Combine(Folder, "webos_rsa");

    /// <summary>The TV's address: the setting, or the "address" file saved beside the key when the TV was set up.</summary>
    private string Address
    {
        get
        {
            if (settings.TvAddress.Length > 0) return settings.TvAddress;
            try { return File.ReadAllText(Path.Combine(Folder, "address")).Trim(); }
            catch (IOException) { return ""; }
        }
    }

    public string Status { get; private set; } = "";
    /// <summary>Renewal failed twice in a row (worth telling the person).</summary>
    public event Action<string>? Failed;

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            if (Address.Length > 0 && File.Exists(KeyFile) && DateTime.Now - settings.TvRenewedAt > TimeSpan.FromHours(20))
                await Renew();
            try { await Task.Delay(TimeSpan.FromHours(1), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    });

    private int _failures;

    public async Task<bool> Renew()
    {
        // A fresh token while the TV answers (it changes if Developer Mode is signed in again); else the kept one.
        if (await ReadToken() is { } fresh) settings.TvSessionToken = fresh;
        if (settings.TvSessionToken.Length == 0)
        {
            Status = "No session token yet: the TV has to be on once";
            return false;
        }
        try
        {
            var reply = await Http.GetStringAsync($"https://developer.lge.com/secure/ResetDevModeSession.dev?sessionToken={Uri.EscapeDataString(settings.TvSessionToken)}");
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.TryGetProperty("result", out var result) && result.GetString() == "success")
            {
                settings.TvRenewedAt = DateTime.Now;
                settings.Save();
                Status = $"Developer Mode renewed {DateTime.Now:d MMM HH:mm}{await TimeLeft()}";
                _failures = 0;
                Log.Write("TV: " + Status);
                return true;
            }
            Status = "LG didn't renew the session: " + reply.Trim()[..Math.Min(120, reply.Trim().Length)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status = "Renewal failed: " + ex.Message;
        }
        Log.Write("TV: " + Status);
        if (++_failures == 2) Failed?.Invoke(Status);
        return false;
    }

    /// <summary>" (1000:00:00 left)": what LG reports remains of the session, hours:minutes:seconds.</summary>
    private async Task<string> TimeLeft()
    {
        try
        {
            using var json = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://developer.lge.com/secure/CheckDevModeSession.dev?sessionToken={Uri.EscapeDataString(settings.TvSessionToken)}"));
            return json.RootElement.TryGetProperty("errorMsg", out var left) && left.GetString() is { } t && t.Contains(':') ? $" ({t} left)" : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "";
        }
    }

    /// <summary>The Developer Mode session token, read from the TV over SSH; null when the TV doesn't answer.</summary>
    private async Task<string?> ReadToken()
    {
        var ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh)) ssh = "ssh";
        var start = new ProcessStartInfo(ssh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-i", KeyFile, "-p", "9922", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
                     "-o", "StrictHostKeyChecking=accept-new", "-o", $"UserKnownHostsFile={Path.Combine(Folder, "known_hosts")}",
                     // webOS's SSH server only offers the older RSA signature.
                     "-o", "HostKeyAlgorithms=+ssh-rsa", "-o", "PubkeyAcceptedAlgorithms=+ssh-rsa",
                     $"prisoner@{Address}", "cat /var/luna/preferences/devmode_enabled",
                 })
            start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var token = output.Trim();
            if (process.ExitCode == 0 && token.Length > 8 && token.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return token;
            if (errors.Length > 0) Log.Write($"TV: couldn't read the session token: {errors.Trim()}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"TV: ssh unavailable: {ex.Message}");
        }
        return null;
    }

    public void Dispose() => _stop.Cancel();
}
