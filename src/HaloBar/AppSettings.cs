using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloBar;

/// <summary>User settings, stored as JSON beside the log.</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Folder, "settings.json");

    /// <summary>Keeps the island out of screenshots, recordings and screen sharing.</summary>
    public bool HideFromCapture { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Hide Windows' own volume pop-up; the island's HUD replaces it.</summary>
    public bool ReplaceVolumePopup { get; set; } = true;
    /// <summary>An island on every monitor, or only on the primary one.</summary>
    public bool AllMonitors { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardHistorySize { get; set; } = 16;
    public bool MirrorNotifications { get; set; } = true;
    public int NotificationSeconds { get; set; } = 5;
    /// <summary>Port for the local API (127.0.0.1 only).</summary>
    public int NotifyPort { get; set; } = 47900;
    public int HoverDelayMs { get; set; } = 150;
    /// <summary>The island's surface: one of <see cref="UI.Looks.Surfaces"/> by id.</summary>
    public string Surface { get; set; } = "black";
    /// <summary>An accent id from <see cref="UI.Looks.Accents"/>, or a custom "#RRGGBB".</summary>
    public string Accent { get; set; } = "auto";
    /// <summary>The music card shows the cover's colours behind it.</summary>
    public bool AlbumCard { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new();
                // 0.1 stored a fixed blue as the default accent; let it follow the surface instead.
                if (settings.Accent == "#7C9CFF") settings.Accent = "auto";
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings)); }
        catch (IOException ex) { Log.Write($"Settings not saved: {ex.Message}"); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;
