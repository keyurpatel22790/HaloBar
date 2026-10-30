using NAudio.CoreAudioApi;

namespace HaloBar.Services;

/// <summary>A saved set of looks and switches: theme, island place, widgets, quiet, sound devices, Night light.</summary>
internal sealed class Profile
{
    public string Name { get; set; } = "";
    public string Surface { get; set; } = "black";
    public string Accent { get; set; } = "auto";
    public int GlassBlur { get; set; } = 35;
    public string TextColour { get; set; } = "auto";
    public string Position { get; set; } = "center";
    public int Size { get; set; } = 100;
    public bool HiddenUntilHover { get; set; }
    public bool DoNotDisturb { get; set; }
    public List<string> HomeTiles { get; set; } = [];
    public List<string> HomeWide { get; set; } = [];
    public List<DesktopWidgetPlace> DesktopWidgets { get; set; } = [];
    /// <summary>Audio endpoint ids; empty leaves the device alone.</summary>
    public string Speaker { get; set; } = "";
    public string Microphone { get; set; } = "";
    public bool? NightLight { get; set; }
}

/// <summary>
/// Work, Gaming and Night profiles. The first tap on one saves how things are now under its name;
/// later taps switch everything back to that.
/// </summary>
internal sealed class ProfileService(AppSettings settings, ControlsService controls, Action save, Action applyLayout)
{
    public static readonly string[] Names = ["Work", "Gaming", "Night"];

    public string Active => settings.ActiveProfile;
    public bool IsSaved(string name) => settings.Profiles.Any(p => p.Name == name);

    /// <summary>Switches to <paramref name="name"/>, or saves the current state as it if it's new. Returns what happened, for a HUD.</summary>
    public string Use(string name)
    {
        if (settings.Profiles.FirstOrDefault(p => p.Name == name) is not { } profile)
        {
            SaveAs(name);
            return "Saved";
        }
        settings.Surface = profile.Surface;
        settings.Accent = profile.Accent;
        settings.GlassBlur = profile.GlassBlur;
        settings.TextColour = profile.TextColour;
        settings.Position = profile.Position;
        settings.Size = profile.Size;
        settings.HiddenUntilHover = profile.HiddenUntilHover;
        settings.DoNotDisturb = profile.DoNotDisturb;
        if (profile.HomeTiles.Count > 0)
        {
            settings.HomeTiles = [.. profile.HomeTiles];
            settings.HomeWide = [.. profile.HomeWide];
        }
        settings.DesktopWidgets = [.. profile.DesktopWidgets.Select(Copy)];
        settings.ActiveProfile = name;
        UI.Prefs.Apply(settings);
        save();
        applyLayout();
        // Devices and Night light last: they're outside HaloBar and may be gone.
        if (profile.Speaker.Length > 0) ControlsService.SetDefaultIfPresent(DataFlow.Render, profile.Speaker);
        if (profile.Microphone.Length > 0) ControlsService.SetDefaultIfPresent(DataFlow.Capture, profile.Microphone);
        if (profile.NightLight is { } night && controls.NightLightOn is { } now && now != night) controls.ToggleNightLight();
        return "On";
    }

    /// <summary>Saves how things are now under <paramref name="name"/> (replacing what it held).</summary>
    public void SaveAs(string name)
    {
        settings.Profiles.RemoveAll(p => p.Name == name);
        settings.Profiles.Add(new Profile
        {
            Name = name,
            Surface = settings.Surface,
            Accent = settings.Accent,
            GlassBlur = settings.GlassBlur,
            TextColour = settings.TextColour,
            Position = settings.Position,
            Size = settings.Size,
            HiddenUntilHover = settings.HiddenUntilHover,
            DoNotDisturb = settings.DoNotDisturb,
            HomeTiles = [.. settings.HomeTiles],
            HomeWide = [.. settings.HomeWide],
            DesktopWidgets = [.. settings.DesktopWidgets.Select(Copy)],
            Speaker = ControlsService.DefaultId(DataFlow.Render) ?? "",
            Microphone = ControlsService.DefaultId(DataFlow.Capture) ?? "",
            NightLight = controls.NightLightOn,
        });
        settings.ActiveProfile = name;
        save();
    }

    /// <summary>The next profile after the active one (the profile hotkey).</summary>
    public string Next()
    {
        var saved = Names.Where(IsSaved).ToArray();
        if (saved.Length == 0) return Names[0];
        var i = Array.IndexOf(saved, Active);
        return saved[(i + 1) % saved.Length];
    }

    private static DesktopWidgetPlace Copy(DesktopWidgetPlace p) => new() { Tile = p.Tile, X = p.X, Y = p.Y, Wide = p.Wide };
}
