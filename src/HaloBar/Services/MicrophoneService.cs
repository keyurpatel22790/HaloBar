using NAudio.CoreAudioApi;

namespace HaloBar.Services;

/// <summary>The default communications microphone's mute state, with a toggle for the mic-mute hotkey.</summary>
internal sealed class MicrophoneService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private MMDevice? _device;
    private MMDeviceNotificationClient? _deviceEvents;

    public bool Available => _device != null;
    public bool Muted { get; private set; }
    public string DeviceName { get; private set; } = "";
    public event Action? Changed;

    public void Start()
    {
        _deviceEvents = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _deviceEvents.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Capture && e.Role == Role.Communications)
                Task.Run(() => { Bind(); Changed?.Invoke(); });
        };
        Bind();
    }

    private void Bind()
    {
        lock (_gate)
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
                _device = null;
            }
            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var device)) return;
            _device = device;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            Muted = _device.AudioEndpointVolume.Mute;
            DeviceName = _device.FriendlyName;
        }
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        if (data.Muted == Muted) return;
        Muted = data.Muted;
        Changed?.Invoke();
    }

    public void ToggleMute()
    {
        lock (_gate)
        {
            if (_device == null) return;
            _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
        }
    }

    public void Dispose()
    {
        (_deviceEvents as IDisposable)?.Dispose();
        lock (_gate) _device?.Dispose();
        _enumerator.Dispose();
    }
}
