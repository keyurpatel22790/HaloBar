using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>
/// Keeps the screen and PC awake while on, by telling Windows the machine is in use
/// (SetThreadExecutionState). Turning it off hands sleep back to Windows' normal timers.
/// </summary>
internal sealed class KeepAwakeService
{
    [Flags]
    private enum Exec : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(Exec flags);

    public bool On { get; private set; }

    public void Set(bool on)
    {
        On = on;
        // Continuous keeps the request standing until changed; with Display the screen stays on too.
        SetThreadExecutionState(on ? Exec.Continuous | Exec.SystemRequired | Exec.DisplayRequired : Exec.Continuous);
    }
}
