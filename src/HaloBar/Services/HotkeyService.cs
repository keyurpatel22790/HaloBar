using static HaloBar.Platform.Native;

namespace HaloBar.Services;

/// <summary>A key with modifiers, as Win32's RegisterHotKey takes it.</summary>
internal readonly record struct Gesture(uint Modifiers, uint Key)
{
    public const uint Win = 0x8;

    /// <summary>"Ctrl+Alt+I", "Win+Shift+F5", "Ctrl+Alt+Num7". Null if the text isn't a shortcut.</summary>
    public static Gesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        uint? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = raw.Length == 0 ? "+" : raw; // "Ctrl++" ends with an empty part
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win" or "windows": mods |= Win; continue;
            }
            if (key != null || KeyCode(part) is not { } vk) return null;
            key = vk;
        }
        // A shortcut needs a key and at least one modifier, or it would swallow normal typing.
        // F-keys on their own are fine.
        if (key is not { } k || (mods == 0 && k is < 0x70 or > 0x87)) return null;
        return new Gesture(mods, k);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Win) != 0) parts.Add("Win");
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static readonly (uint Vk, string Name)[] Named =
    [
        (0x20, "Space"), (0x0D, "Enter"), (0x09, "Tab"), (0x08, "Backspace"), (0x2E, "Delete"), (0x2D, "Insert"),
        (0x24, "Home"), (0x23, "End"), (0x21, "PageUp"), (0x22, "PageDown"),
        (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x2C, "PrintScreen"), (0x13, "Pause"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
        (0x6A, "Num*"), (0x6B, "Num+"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/"),
        (0xAD, "Mute"), (0xAE, "VolumeDown"), (0xAF, "VolumeUp"), (0xB0, "NextTrack"), (0xB1, "PrevTrack"), (0xB3, "PlayPause"),
    ];

    public static string KeyName(uint vk) => vk switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => Named.FirstOrDefault(n => n.Vk == vk).Name ?? $"Key{vk}",
    };

    public static uint? KeyCode(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsAsciiDigit(name[3])) return (uint)(0x60 + name[3] - '0');
        foreach (var (vk, n) in Named)
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return vk;
        return null;
    }

    /// <summary>Keys that only modify; capturing waits past these for the real key.</summary>
    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}

/// <summary>Something a shortcut can do.</summary>
internal sealed record HotkeyAction(string Id, string Label, string Default);

/// <summary>
/// The system-wide shortcuts. Each action's keys live in <see cref="AppSettings.Hotkeys"/> as
/// text ("Ctrl+Alt+I"); an action with no entry uses its default and an empty entry means none.
/// Registration is per action on the hidden host window; one another app already holds is
/// reported as taken so the Settings panel can say so.
/// </summary>
internal sealed class HotkeyService(AppSettings settings, IntPtr hwnd, Action? save = null)
{
    private readonly Action _save = save ?? settings.Save;

    public static readonly HotkeyAction[] Actions =
    [
        new("open", "Open the island", "Ctrl+Alt+I"),
        new("playpause", "Play or pause", "Ctrl+Alt+P"),
        new("next", "Next track", ""),
        new("previous", "Previous track", ""),
        new("timer", "Start or pause the focus timer", "Ctrl+Alt+T"),
        new("hide", "Hide the island", "Ctrl+Alt+H"),
        new("mic", "Mute the microphone", "Ctrl+Alt+M"),
        new("reminder", "New reminder", "Ctrl+Alt+E"),
        new("output", "Next sound output", "Ctrl+Alt+O"),
        new("color", "Pick a colour", "Ctrl+Alt+C"),
        new("screenshot", "Take a screenshot", "Ctrl+Alt+F"),
        new("screentext", "Copy text from the screen", "Ctrl+Alt+X"),
        new("qr", "Scan a QR code on the screen", "Ctrl+Alt+Q"),
        new("record", "Record the screen (again to stop)", "Ctrl+Alt+V"),
        new("obs", "OBS: start or stop recording", "Ctrl+Alt+R"),
        new("web", "Show or hide the web drawer", "Ctrl+Alt+W"),
        new("controls", "Open Controls (Wi-Fi, Bluetooth, sound)", "Ctrl+Alt+K"),
        new("sound-output", "Switch sound output device", ""),
        new("clipboard", "Search the clipboard history", "Ctrl+Alt+B"),
        new("profile", "Switch to the next profile", ""),
        new("dnd", "Do not disturb on or off", ""),
        new("sharing", "Show in screenshots and sharing on or off", "Ctrl+Alt+J"),
        new("launch1", "Open app in slot 1", ""), new("launch2", "Open app in slot 2", ""), new("launch3", "Open app in slot 3", ""),
        new("launch4", "Open app in slot 4", ""), new("launch5", "Open app in slot 5", ""), new("launch6", "Open app in slot 6", ""),
    ];

    private readonly HashSet<int> _registered = [];
    private readonly HashSet<string> _taken = [];

    public event Action<string>? Pressed;

    public Gesture? Get(string id)
    {
        if (settings.Hotkeys.TryGetValue(id, out var text)) return Gesture.Parse(text);
        return Gesture.Parse(Actions.FirstOrDefault(a => a.Id == id)?.Default);
    }

    /// <summary>The shortcut as text, or "" when the action has none.</summary>
    public string Text(string id) => Get(id)?.ToString() ?? "";

    /// <summary>Another app already uses this action's keys.</summary>
    public bool Taken(string id) => _taken.Contains(id);

    /// <summary>
    /// Gives <paramref name="id"/> these keys (null clears them). Another HaloBar action that had
    /// the same keys loses them, so one press never means two things.
    /// </summary>
    public void Set(string id, Gesture? gesture)
    {
        if (gesture is { } g)
            foreach (var other in Actions)
                if (other.Id != id && Get(other.Id) == g) settings.Hotkeys[other.Id] = "";
        var fallback = Gesture.Parse(Actions.FirstOrDefault(a => a.Id == id)?.Default);
        if (gesture == fallback) settings.Hotkeys.Remove(id); // back to the default: keep the file tidy
        else settings.Hotkeys[id] = gesture?.ToString() ?? "";
        _save();
    }

    public void Reset()
    {
        settings.Hotkeys.Clear();
        _save();
    }

    /// <summary>Registers every action's keys, replacing what was registered before.</summary>
    public void Register()
    {
        Unregister();
        _taken.Clear();
        for (var i = 0; i < Actions.Length; i++)
        {
            if (Get(Actions[i].Id) is not { } g) continue;
            if (RegisterHotKey(hwnd, i + 1, g.Modifiers | MOD_NOREPEAT, g.Key)) _registered.Add(i + 1);
            else
            {
                _taken.Add(Actions[i].Id);
                Log.Write($"Hotkey {g} ({Actions[i].Label}) is taken by another app");
            }
        }
    }

    /// <summary>Lets every shortcut go, so the keys can be pressed while choosing new ones.</summary>
    public void Unregister()
    {
        foreach (var id in _registered) UnregisterHotKey(hwnd, id);
        _registered.Clear();
    }

    /// <summary>Call with WM_HOTKEY's id.</summary>
    public void OnHotkey(int id)
    {
        if (id >= 1 && id <= Actions.Length) Pressed?.Invoke(Actions[id - 1].Id);
    }
}
