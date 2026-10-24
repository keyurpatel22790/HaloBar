using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace HaloBar.Services;

/// <summary>Tells when paired Bluetooth devices (headphones, mice, controllers) connect or disconnect.</summary>
internal sealed class BluetoothService : IDisposable
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<string, (string Name, bool Connected)> _devices = [];
    private readonly object _gate = new();
    private bool _enumerated;

    /// <summary>(device name, connected).</summary>
    public event Action<string, bool>? ConnectionChanged;

    public IReadOnlyList<string> Connected
    {
        get { lock (_gate) return [.. _devices.Values.Where(d => d.Connected).Select(d => d.Name)]; }
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
        var watcher = DeviceInformation.CreateWatcher(selector, [IsConnected], DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, d) => Upsert(d.Id, d.Name, d.Properties);
        watcher.Updated += (_, u) => Upsert(u.Id, null, u.Properties);
        watcher.Removed += (_, u) => { lock (_gate) _devices.Remove(u.Id); };
        // Devices already connected at start are not news.
        watcher.EnumerationCompleted += (_, _) => _enumerated = true;
        watcher.Start();
        _watchers.Add(watcher);
    }

    private void Upsert(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        if (!props.TryGetValue(IsConnected, out var value) || value is not bool connected) return;
        bool changed;
        string deviceName;
        lock (_gate)
        {
            var known = _devices.TryGetValue(id, out var old);
            deviceName = name ?? (known ? old.Name : "Bluetooth device");
            changed = known && old.Connected != connected;
            _devices[id] = (deviceName, connected);
        }
        if (changed && _enumerated && !string.IsNullOrWhiteSpace(deviceName)) ConnectionChanged?.Invoke(deviceName, connected);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { }
        }
    }
}
