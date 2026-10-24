namespace HaloBar.Services;

internal enum TimerPhase { Idle, Focus, Break }

/// <summary>
/// A Pomodoro timer: focus for N minutes, then a short break, then focus again. Pausing keeps
/// the time left. Ticked from the frame loop.
/// </summary>
internal sealed class TimerService(AppSettings settings)
{
    private DateTime _endsAt;
    private TimeSpan _left;

    public TimerPhase Phase { get; private set; } = TimerPhase.Idle;
    public bool Paused { get; private set; }
    public bool Running => Phase != TimerPhase.Idle && !Paused;

    public TimeSpan Length => TimeSpan.FromMinutes(Phase == TimerPhase.Break ? settings.BreakMinutes : settings.FocusMinutes);
    public TimeSpan Remaining => Phase == TimerPhase.Idle ? Length : Paused ? _left : Max(_endsAt - DateTime.Now, TimeSpan.Zero);
    public float Progress => Phase == TimerPhase.Idle ? 0 : (float)(1 - Remaining / Length);

    /// <summary>The phase that just ended.</summary>
    public event Action<TimerPhase>? Finished;

    public void Toggle()
    {
        if (Phase == TimerPhase.Idle) Begin(TimerPhase.Focus);
        else if (Paused)
        {
            _endsAt = DateTime.Now + _left;
            Paused = false;
        }
        else
        {
            _left = Remaining;
            Paused = true;
        }
    }

    public void Reset()
    {
        Phase = TimerPhase.Idle;
        Paused = false;
    }

    /// <summary>Ends the current phase now and moves to the next.</summary>
    public void Skip() => Begin(Phase == TimerPhase.Focus ? TimerPhase.Break : TimerPhase.Focus);

    /// <summary>Changes the focus length by <paramref name="minutes"/> while idle.</summary>
    public void Step(int minutes)
    {
        settings.FocusMinutes = Math.Clamp(settings.FocusMinutes + minutes, 1, 180);
        settings.Save();
    }

    private void Begin(TimerPhase phase)
    {
        Phase = phase;
        Paused = false;
        _endsAt = DateTime.Now + Length;
    }

    public void Tick()
    {
        if (!Running || DateTime.Now < _endsAt) return;
        var ended = Phase;
        Begin(ended == TimerPhase.Focus ? TimerPhase.Break : TimerPhase.Focus);
        // After a break, wait for the user rather than starting the next focus on its own.
        if (ended == TimerPhase.Break) Reset();
        Finished?.Invoke(ended);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
