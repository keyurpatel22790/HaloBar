using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace HaloBar.Services;

/// <summary>A Bluetooth device as the Controls panel lists it.</summary>
internal sealed record BluetoothItem(string Id, string Name, bool Connected, Guid? Container);

/// <summary>
/// Paired Bluetooth devices (headphones, mice, controllers): tells when they connect or disconnect,
/// connects and disconnects audio devices, forgets them, and finds and pairs new ones.
/// </summary>
internal sealed class BluetoothService : IDisposable
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string ContainerId = "System.Devices.Aep.ContainerId";
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<string, BluetoothItem> _devices = [];
    private readonly Dictionary<string, BluetoothItem> _nearby = [];
    private readonly List<DeviceWatcher> _searchers = [];
    private readonly object _gate = new();
    private bool _enumerated;

    /// <summary>(device name, connected).</summary>
    public event Action<string, bool>? ConnectionChanged;
    /// <summary>The device lists, the search or <see cref="Status"/> changed.</summary>
    public event Action? Changed;

    /// <summary>What's happening right now ("Pairing with…", "Couldn't connect"), or null.</summary>
    public string? Status { get; private set; }
    /// <summary>The code to compare on the other device while pairing, or null.</summary>
    public string? Pin { get; private set; }
    public bool Searching => _searchers.Count > 0;
    /// <summary>Ids of devices with a connect, disconnect, pair or forget in progress.</summary>
    public IReadOnlyCollection<string> Busy { get { lock (_gate) return [.. _busy]; } }
    private readonly HashSet<string> _busy = [];

    public IReadOnlyList<string> Connected
    {
        get { lock (_gate) return [.. _devices.Values.Where(d => d.Connected).DistinctBy(d => d.Container?.ToString() ?? d.Id).Select(d => d.Name)]; }
    }

    /// <summary>Paired devices, connected ones first.</summary>
    public IReadOnlyList<BluetoothItem> Paired
    {
        get
        {
            lock (_gate)
                return [.. _devices.Values.Where(d => !string.IsNullOrWhiteSpace(d.Name))
                    .GroupBy(d => d.Container?.ToString() ?? d.Id).Select(g => g.OrderByDescending(d => d.Connected).First())
                    .OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>Unpaired devices found by the current search.</summary>
    public IReadOnlyList<BluetoothItem> Nearby
    {
        get
        {
            lock (_gate)
            {
                var paired = _devices.Values.Select(d => d.Name).ToHashSet();
                return [.. _nearby.Values.Where(d => !paired.Contains(d.Name))
                    .DistinctBy(d => d.Name).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public void Start()
    {
        try
        {
            Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            Log.Write($"Bluetooth: unavailable: {ex.Message}");
        }
    }

    private void Watch(string selector)
    {
        var watcher = DeviceInformation.CreateWatcher(selector, [IsConnected, ContainerId], DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, d) => Upsert(d.Id, d.Name, d.Properties);
        watcher.Updated += (_, u) => Upsert(u.Id, null, u.Properties);
        watcher.Removed += (_, u) =>
        {
            lock (_gate) _devices.Remove(u.Id);
            Changed?.Invoke();
        };
        // Devices already connected at start are not news.
        watcher.EnumerationCompleted += (_, _) => _enumerated = true;
        watcher.Start();
        _watchers.Add(watcher);
    }

    private void Upsert(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        bool changed, connected;
        string deviceName;
        lock (_gate)
        {
            var known = _devices.TryGetValue(id, out var old);
            connected = props.TryGetValue(IsConnected, out var value) && value is bool b ? b : old?.Connected ?? false;
            var container = props.TryGetValue(ContainerId, out var c) && c is Guid g ? g : old?.Container;
            deviceName = name ?? old?.Name ?? "Bluetooth device";
            changed = known && old!.Connected != connected;
            _devices[id] = new BluetoothItem(id, deviceName, connected, container);
        }
        if (changed && _enumerated && !string.IsNullOrWhiteSpace(deviceName)) ConnectionChanged?.Invoke(deviceName, connected);
        Changed?.Invoke();
    }

    // ───────────────────────────── connect and disconnect ─────────────────────────────

    /// <summary>
    /// Connects or disconnects a paired device. Audio devices get the request through their
    /// Bluetooth audio driver; a device whose driver Windows has unloaded (it was away) is first
    /// paged, which brings a paired headset that's switched on back. Windows has no way for apps
    /// to disconnect other kinds of device.
    /// </summary>
    public void SetConnected(BluetoothItem device, bool connect) => Run(device.Id, async () =>
    {
        Status = connect ? $"Connecting {device.Name}…" : $"Disconnecting {device.Name}…";
        Changed?.Invoke();
        var done = await Task.Run(() => BluetoothAudio.Set(device.Container, device.Name, connect));
        if (!connect)
        {
            Status = done ? null : $"Windows only lets apps disconnect headphones and speakers: use {device.Name} itself or Settings";
            return;
        }
        if (!done)
        {
            if (!await Page(device.Id))
            {
                Status = $"{device.Name} didn't answer. Is it on and nearby?";
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Task.Run(() => BluetoothAudio.Set(device.Container, device.Name, true));
        }
        // Headphones take a few seconds; say so if they never show up.
        for (var i = 0; i < 12 && !IsConnectedNow(device.Id); i++) await Task.Delay(TimeSpan.FromSeconds(1));
        Status = IsConnectedNow(device.Id) ? null : $"{device.Name} didn't connect. Is it on and nearby?";
    });

    private bool IsConnectedNow(string id)
    {
        lock (_gate) return _devices.TryGetValue(id, out var now) && now.Connected;
    }

    /// <summary>Opens a baseband link by asking the device for its services afresh. True if it answered.</summary>
    private static async Task<bool> Page(string id)
    {
        using var device = await BluetoothDevice.FromIdAsync(id);
        if (device == null) return false;
        var services = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
        return services.Error == BluetoothError.Success;
    }

    /// <summary>Removes the pairing.</summary>
    public void Forget(BluetoothItem device) => Run(device.Id, async () =>
    {
        var info = await DeviceInformation.CreateFromIdAsync(device.Id);
        var result = await info.Pairing.UnpairAsync();
        Status = result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired
            ? null : $"Couldn't forget {device.Name} ({result.Status})";
    });

    /// <summary>Pairs with a device found by the search, accepting the code it shows.</summary>
    public void Pair(BluetoothItem device) => Run(device.Id, async () =>
    {
        Status = $"Pairing with {device.Name}…";
        Changed?.Invoke();
        var info = await DeviceInformation.CreateFromIdAsync(device.Id);
        var custom = info.Pairing.Custom;
        custom.PairingRequested += (_, e) =>
        {
            // The person chose this device here; a code to compare is shown on the island.
            if (e.PairingKind is DevicePairingKinds.DisplayPin or DevicePairingKinds.ConfirmPinMatch)
            {
                Pin = e.Pin;
                Changed?.Invoke();
            }
            e.Accept();
        };
        var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch);
        Pin = null;
        Status = result.Status switch
        {
            DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => null,
            DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled
                => $"Pairing with {device.Name} was cancelled",
            DevicePairingResultStatus.RequiredHandlerNotRegistered
                => $"{device.Name} needs a PIN typed in: pair it from Windows Settings",
            _ => $"Couldn't pair with {device.Name} ({result.Status})",
        };
        if (result.Status == DevicePairingResultStatus.Paired)
            lock (_gate) _nearby.Remove(device.Id);
    });

    private void Run(string id, Func<Task> work)
    {
        lock (_gate)
            if (!_busy.Add(id)) return;
        Changed?.Invoke();
        // All of it off the window thread: an await there never resumes.
        _ = Task.Run(() => Finish(id, work));
    }

    private async Task Finish(string id, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Status = "Bluetooth: " + ex.Message.Trim();
            Log.Write($"Bluetooth: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            lock (_gate) _busy.Remove(id);
            Changed?.Invoke();
        }
    }

    public void ClearStatus()
    {
        Status = null;
        Changed?.Invoke();
    }

    // ───────────────────────────── finding new devices ─────────────────────────────

    /// <summary>Looks for devices in pairing mode for half a minute.</summary>
    public void Search()
    {
        if (Searching) return;
        lock (_gate) _nearby.Clear();
        try
        {
            foreach (var selector in new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(false), BluetoothLEDevice.GetDeviceSelectorFromPairingState(false) })
            {
                var watcher = DeviceInformation.CreateWatcher(selector, [ContainerId], DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, d) =>
                {
                    // Nameless LE beacons (phones, trackers nearby) aren't anything to pair with.
                    if (string.IsNullOrWhiteSpace(d.Name) || d.Name.StartsWith("Bluetooth ", StringComparison.Ordinal) || !d.Pairing.CanPair) return;
                    lock (_gate) _nearby[d.Id] = new BluetoothItem(d.Id, d.Name, false, null);
                    Changed?.Invoke();
                };
                watcher.Removed += (_, u) =>
                {
                    lock (_gate) _nearby.Remove(u.Id);
                    Changed?.Invoke();
                };
                watcher.Start();
                _searchers.Add(watcher);
            }
        }
        catch (Exception ex)
        {
            Status = "Can't search: " + ex.Message.Trim();
        }
        Changed?.Invoke();
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => StopSearch());
    }

    public void StopSearch()
    {
        foreach (var w in _searchers.ToArray())
        {
            try { w.Stop(); } catch { }
        }
        _searchers.Clear();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        StopSearch();
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { }
        }
    }
}
