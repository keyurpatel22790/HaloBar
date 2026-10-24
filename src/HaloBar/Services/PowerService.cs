using Windows.System.Power;

namespace HaloBar.Services;

internal enum PowerEvent { PluggedIn, Unplugged, Low, Critical, Full }

/// <summary>Battery level, charging state and time left, from Windows' power manager.</summary>
internal sealed class PowerService : IDisposable
{
    private bool _wasPlugged;
    private int _lastWarned = 101;

    public bool HasBattery { get; private set; }
    public int Percent { get; private set; } = -1;
    public bool Charging { get; private set; }
    public bool PluggedIn { get; private set; }
    /// <summary>Windows' own estimate while on battery; null when unknown or charging.</summary>
    public TimeSpan? TimeLeft { get; private set; }

    public event Action? Changed;
    public event Action<PowerEvent>? Event;

    public void Start()
    {
        Read();
        _wasPlugged = PluggedIn;
        PowerManager.BatteryStatusChanged += OnChanged;
        PowerManager.PowerSupplyStatusChanged += OnChanged;
        PowerManager.RemainingChargePercentChanged += OnChanged;
        PowerManager.RemainingDischargeTimeChanged += OnChanged;
    }

    private void OnChanged(object? sender, object e)
    {
        Read();
        if (PluggedIn != _wasPlugged)
        {
            _wasPlugged = PluggedIn;
            if (HasBattery) Event?.Invoke(PluggedIn ? PowerEvent.PluggedIn : PowerEvent.Unplugged);
            _lastWarned = 101;
        }
        if (HasBattery && !PluggedIn)
        {
            // Warn once at 20% and again at 10%.
            if (Percent <= 10 && _lastWarned > 10) { _lastWarned = 10; Event?.Invoke(PowerEvent.Critical); }
            else if (Percent <= 20 && _lastWarned > 20) { _lastWarned = 20; Event?.Invoke(PowerEvent.Low); }
        }
        if (HasBattery && PluggedIn && Percent >= 100 && _lastWarned != 100)
        {
            _lastWarned = 100;
            Event?.Invoke(PowerEvent.Full);
        }
        Changed?.Invoke();
    }

    private void Read()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            HasBattery = status != BatteryStatus.NotPresent;
            Percent = HasBattery ? PowerManager.RemainingChargePercent : -1;
            Charging = status == BatteryStatus.Charging;
            PluggedIn = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            var left = PowerManager.RemainingDischargeTime;
            TimeLeft = !PluggedIn && left > TimeSpan.Zero && left < TimeSpan.FromDays(2) ? left : null;
        }
        catch (Exception ex)
        {
            Log.Write($"Power: {ex.Message}");
        }
    }

    public void Dispose()
    {
        PowerManager.BatteryStatusChanged -= OnChanged;
        PowerManager.PowerSupplyStatusChanged -= OnChanged;
        PowerManager.RemainingChargePercentChanged -= OnChanged;
        PowerManager.RemainingDischargeTimeChanged -= OnChanged;
    }
}
