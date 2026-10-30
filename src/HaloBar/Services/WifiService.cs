using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace HaloBar.Services;

/// <summary>A Wi-Fi network as the Controls panel lists it.</summary>
internal sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, bool Saved);

/// <summary>Lists nearby Wi-Fi networks and connects to or disconnects from them.</summary>
internal sealed class WifiService
{
    private WiFiAdapter? _adapter;
    private Dictionary<string, WiFiAvailableNetwork> _found = [];
    private bool _working;

    public IReadOnlyList<WifiNetwork> Networks { get; private set; } = [];
    public string? ConnectedSsid { get; private set; }
    public bool Scanning { get; private set; }
    /// <summary>What's happening right now ("Connecting to…", "Wrong password"), or null.</summary>
    public string? Status { get; private set; }
    /// <summary>The secured network that needs a password typed in, or null.</summary>
    public string? NeedsPassword { get; private set; }
    public event Action? Changed;

    private async Task<WiFiAdapter?> Adapter()
    {
        if (_adapter != null) return _adapter;
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
        {
            Status = "Windows didn't allow HaloBar to use Wi-Fi";
            return null;
        }
        _adapter = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
        if (_adapter == null) Status = "No Wi-Fi adapter";
        else _adapter.AvailableNetworksChanged += (_, _) => Read();
        return _adapter;
    }

    /// <summary>Scans for networks (a few seconds), then lists them strongest first.</summary>
    public void Scan() => Scan(force: true);

    /// <summary>Scans unless a scan finished in the last half minute (opening the list again).</summary>
    public void ScanIfStale() => Scan(force: false);

    private DateTime _scanned;

    private void Scan(bool force)
    {
        if (Scanning || !force && DateTime.Now - _scanned < TimeSpan.FromSeconds(30)) return;
        Scanning = true;
        Changed?.Invoke();
        // All of it off the window thread: an await there never resumes.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await Adapter() is not { } adapter) return;
                // Networks also arrive through AvailableNetworksChanged, so a slow scan only delays the status.
                await Task.WhenAny(adapter.ScanAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(10)));
                Read();
            }
            catch (Exception ex)
            {
                // Scanning fails while the radio is off.
                Status = ex.HResult == unchecked((int)0x8007048F) ? "Wi-Fi is off" : "Couldn't scan: " + ex.Message.Trim();
                Log.Write($"Wi-Fi: scan failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                _scanned = DateTime.Now;
                Scanning = false;
                Changed?.Invoke();
            }
        });
    }

    /// <summary>Reads which network is connected and the last scan, without scanning.</summary>
    public void Refresh() => Task.Run(async () =>
    {
        try
        {
            if (await Adapter() is null) return;
        }
        catch (Exception ex)
        {
            Log.Write($"Wi-Fi: unavailable: {ex.Message}");
            return;
        }
        Read();
    });

    private async void Read()
    {
        try
        {
            if (_adapter == null) return;
            var profile = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
            ConnectedSsid = profile?.IsWlanConnectionProfile == true ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : null;
            // Windows names a saved network's profile after its SSID.
            var saved = NetworkInformation.GetConnectionProfiles().Where(p => p.IsWlanConnectionProfile).Select(p => p.ProfileName).ToHashSet();
            var found = _adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrEmpty(n.Ssid))
                .GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .ToDictionary(n => n.Ssid);
            _found = found;
            Networks = [.. found.Values
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                    n.Ssid == ConnectedSsid, saved.Contains(n.Ssid)))
                .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Saved).ThenByDescending(n => n.Bars)];
        }
        catch (Exception ex)
        {
            Log.Write($"Wi-Fi: reading networks failed: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Connects to a network. A secured one that Windows hasn't saved needs <paramref name="password"/>;
    /// without one this asks for it (<see cref="NeedsPassword"/>) instead.
    /// </summary>
    public void Connect(WifiNetwork network, string? password = null) => Task.Run(() => ConnectAsync(network, password));

    private async Task ConnectAsync(WifiNetwork network, string? password)
    {
        if (_working || _adapter == null || !_found.TryGetValue(network.Ssid, out var raw)) return;
        if (network.Secured && !network.Saved && string.IsNullOrEmpty(password))
        {
            NeedsPassword = network.Ssid;
            Status = null;
            Changed?.Invoke();
            return;
        }
        _working = true;
        NeedsPassword = null;
        Status = $"Connecting to {network.Ssid}…";
        Changed?.Invoke();
        try
        {
            var result = string.IsNullOrEmpty(password)
                ? await _adapter.ConnectAsync(raw, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(raw, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            switch (result.ConnectionStatus)
            {
                case WiFiConnectionStatus.Success:
                    Status = null;
                    break;
                case WiFiConnectionStatus.InvalidCredential:
                    // A saved password that has changed, or a mistyped one: ask (again).
                    NeedsPassword = network.Ssid;
                    Status = string.IsNullOrEmpty(password) ? null : "Wrong password";
                    break;
                case WiFiConnectionStatus.Timeout:
                    Status = $"{network.Ssid} didn't answer";
                    break;
                default:
                    Status = $"Couldn't connect to {network.Ssid} ({result.ConnectionStatus})";
                    break;
            }
        }
        catch (Exception ex)
        {
            Status = $"Couldn't connect to {network.Ssid}";
            Log.Write($"Wi-Fi: connect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            _working = false;
            Read();
        }
    }

    public void Disconnect() => Task.Run(() =>
    {
        try
        {
            _adapter?.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Write($"Wi-Fi: disconnect failed: {ex.Message}");
        }
        Read();
    });

    public void CancelPassword()
    {
        NeedsPassword = null;
        Status = null;
        Changed?.Invoke();
    }
}
