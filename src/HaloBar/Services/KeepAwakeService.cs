using System.Runtime.InteropServices;

namespace HaloBar.Services;

/// <summary>
/// Keeps the screen and PC awake while on. Two Windows timers have to be held off, and each needs
/// something different, so caffeine does both:
///   • A <b>power request</b> says the machine is in use, so it won't sleep or turn the screen off.
///     It is process-wide (not tied to one thread, as the old SetThreadExecutionState call was) and,
///     with the execution flag, also holds off Modern Standby on the laptops that use it.
///   • A <b>harmless key tap</b> (F15 — a key no app uses) once a minute resets the <i>user-idle</i>
///     clock, so the screen saver and the automatic lock stay away. A power request can't stop those;
///     only looking like activity does.
/// Turning it off releases the request and stops the taps, handing sleep back to Windows' timers.
/// </summary>
internal sealed class KeepAwakeService : IDisposable
{
    public bool On { get; private set; }

    public void Set(bool on)
    {
        if (on == On) return;
        On = on;
        if (on)
        {
            Hold();
            _nudge = new Timer(_ => Nudge(), null, NudgeEvery, NudgeEvery);
        }
        else
        {
            _nudge?.Dispose();
            _nudge = null;
            Release();
        }
    }

    // ── power request: no sleep, no screen-off, no Modern Standby ─────────────
    private nint _request;

    private void Hold()
    {
        var ctx = new ReasonContext
        {
            Version = 0, // POWER_REQUEST_CONTEXT_VERSION
            Flags = PowerRequestContextSimpleString,
            Reason = "HaloBar — keep awake",
        };
        _request = PowerCreateRequest(ref ctx);
        if (_request == 0 || _request == -1)
        {
            // Couldn't make a power request; fall back to the old call so at least sleep is held off.
            _request = 0;
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            Log.Write("Keep awake: power request unavailable, using execution state only");
            return;
        }
        PowerSetRequest(_request, PowerRequestType.SystemRequired);
        PowerSetRequest(_request, PowerRequestType.DisplayRequired);
        PowerSetRequest(_request, PowerRequestType.ExecutionRequired);
    }

    private void Release()
    {
        if (_request != 0)
        {
            PowerClearRequest(_request, PowerRequestType.ExecutionRequired);
            PowerClearRequest(_request, PowerRequestType.DisplayRequired);
            PowerClearRequest(_request, PowerRequestType.SystemRequired);
            CloseHandle(_request);
            _request = 0;
        }
        SetThreadExecutionState(EsContinuous); // drop any fallback request
    }

    // ── idle nudge: no screen saver, no auto-lock ────────────────────────────
    private static readonly TimeSpan NudgeEvery = TimeSpan.FromSeconds(50);
    private Timer? _nudge;

    private static void Nudge()
    {
        // F15 is a key no app reacts to; tapping it resets the idle clock without doing anything.
        var keys = new[] { Key(VkF15, down: true), Key(VkF15, down: false) };
        SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => Set(false);

    // ── interop ──────────────────────────────────────────────────────────────
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const ushort VkF15 = 0x7E;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x2;

    private enum PowerRequestType { DisplayRequired = 0, SystemRequired = 1, AwayModeRequired = 2, ExecutionRequired = 3 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    private static INPUT Key(ushort vk, bool down) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KeyEventKeyUp } },
    };

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint request, PowerRequestType type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint request, PowerRequestType type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
