using NAudio.CoreAudioApi;

namespace HaloBar.Services;

/// <summary>
/// Watches the default speaker's volume and mute state, and reads its live output level
/// (used to drive the music visualiser). Follows the default device when it changes.
/// </summary>
internal sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private MMDevice? _device;
    private MMDeviceNotificationClient? _deviceEvents;

    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Raised on any volume or mute change, from whatever thread Windows uses.</summary>
    public event Action? Changed;

    public void Start()
    {
        _deviceEvents = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _deviceEvents.DefaultDeviceChanged += OnDefaultDeviceChanged;
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
            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
                Volume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                Muted = _device.AudioEndpointVolume.Mute;
                DeviceName = _device.FriendlyName;
            }
            catch (Exception ex)
            {
                Log.Write($"Volume: no output device: {ex.Message}");
            }
        }
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        Volume = data.MasterVolume;
        Muted = data.Muted;
        Changed?.Invoke();
    }

    /// <summary>Peak output level 0..1 right now.</summary>
    public float Peak
    {
        get
        {
            lock (_gate)
            {
                try { return _device?.AudioMeterInformation.MasterPeakValue ?? 0; }
                catch { return 0; }
            }
        }
    }

    public void Nudge(float delta)
    {
        lock (_gate)
        {
            if (_device == null) return;
            var v = Math.Clamp(_device.AudioEndpointVolume.MasterVolumeLevelScalar + delta, 0f, 1f);
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            if (v > 0 && _device.AudioEndpointVolume.Mute) _device.AudioEndpointVolume.Mute = false;
        }
    }

    public void ToggleMute()
    {
        lock (_gate)
        {
            if (_device != null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia) return;
        // Rebinding inside the callback can deadlock the audio service; do it on the pool.
        Task.Run(() =>
        {
            Bind();
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        (_deviceEvents as IDisposable)?.Dispose();
        lock (_gate) _device?.Dispose();
        _enumerator.Dispose();
    }
}
