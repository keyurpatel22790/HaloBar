using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloBar;

/// <summary>User settings, stored as JSON beside the log.</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Folder, "settings.json");

    /// <summary>
    /// The island shows in screen sharing, screenshots and other apps' recordings. Off, it's kept out
    /// of them, which the glass lens needs (it copies the screen behind it); on, glass uses Windows' blur.
    /// </summary>
    public bool ShowInSharing { get; set; }
    /// <summary>A code from <see cref="UI.Lang.Available"/>; "auto" follows Windows.</summary>
    public string Language { get; set; } = "auto";
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Hide Windows' own volume pop-up; the island's HUD replaces it.</summary>
    public bool ReplaceVolumePopup { get; set; } = true;
    /// <summary>An island on every monitor, or only on the primary one.</summary>
    public bool AllMonitors { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardHistorySize { get; set; } = 16;
    public bool MirrorNotifications { get; set; } = true;
    /// <summary>Notifications, chat and agent pings wait in the list instead of popping up.</summary>
    public bool DoNotDisturb { get; set; }
    /// <summary>Do not disturb while a focus-timer session runs.</summary>
    public bool QuietDuringFocus { get; set; } = true;
    /// <summary>Turn off Windows' own banner for apps whose notifications the island shows.</summary>
    public bool QuietWindowsBanners { get; set; }
    /// <summary>Banner switches HaloBar turned off: app id → the ShowBanner value before (-1 = none).</summary>
    public Dictionary<string, int> QuietedBanners { get; set; } = [];
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

    // HUDs: quick read-outs that take over the pill for a moment.
    public bool ShowVolumeHud { get; set; } = true;
    public bool ShowBrightnessHud { get; set; } = true;
    public bool ShowBatteryHud { get; set; } = true;
    public bool ShowBluetoothHud { get; set; } = true;
    public bool ShowLockKeysHud { get; set; } = true;
    public bool ShowMicHud { get; set; } = true;
    /// <summary>Green and orange dots on the pill while the camera or microphone is in use.</summary>
    public bool ShowPrivacyIndicator { get; set; } = true;
    public bool ShowBatteryEstimate { get; set; } = true;

    // Music.
    /// <summary>The app whose music the island shows, by app id; "" for whichever plays.</summary>
    public string MusicSource { get; set; } = "";
    /// <summary>Apps never shown on the island (a browser playing videos, say).</summary>
    public List<string> MusicHidden { get; set; } = [];
    /// <summary>Apps seen playing, app id → name, for the switches in Settings.</summary>
    public Dictionary<string, string> MusicSeen { get; set; } = [];
    public bool ShowLyrics { get; set; } = true;
    /// <summary>"bars" or "pixels": what plays beside the artwork on the pill.</summary>
    public string Visualizer { get; set; } = "bars";
    /// <summary>"simple", "wave", "pixels", "comet" or "hearts".</summary>
    public string ProgressBar { get; set; } = "wave";

    // Home widgets.
    /// <summary>Tiles on the Home panel, in order; four slots to a page.</summary>
    public List<string> HomeTiles { get; set; } = ["date", "weather", "stats", "timer"];
    /// <summary>Tiles that take two slots.</summary>
    public List<string> HomeWide { get; set; } = [];
    public string WeatherCity { get; set; } = "";
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }
    public bool WeatherFahrenheit { get; set; }
    /// <summary>Colourful tints the weather tile like the sky; otherwise line icons and plain type.</summary>
    public bool WeatherColourful { get; set; } = true;
    public string WorldClockCity { get; set; } = "London";
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public string CalendarIcsUrl { get; set; } = "";
    /// <summary>Show a countdown on the pill in the minutes before a meeting.</summary>
    public bool MeetingCountdown { get; set; } = true;
    public List<Services.Reminder> Reminders { get; set; } = [];
    public List<Services.TodoItem> Todos { get; set; } = [];
    public string TickerSymbols { get; set; } = "BTC, ETH, ^NSEI";
    /// <summary>Price alerts as "BTC > 70000; AAPL < 180".</summary>
    public string PriceAlerts { get; set; } = "";
    public string PhotosFolder { get; set; } = "";
    /// <summary>Home tiles pinned to the desktop as widgets.</summary>
    public List<DesktopWidgetPlace> DesktopWidgets { get; set; } = [];
    public string NoteText { get; set; } = "";
    public string CountdownTitle { get; set; } = "";
    public DateTime? CountdownDate { get; set; }
    public string CounterName { get; set; } = "Count";
    public int CounterValue { get; set; }
    public string TranslateFrom { get; set; } = "auto";
    public string TranslateTo { get; set; } = "gu";
    public string CurrencyBase { get; set; } = "USD";
    public string CurrencyTargets { get; set; } = "INR, EUR, GBP";
    public List<string> GitFolders { get; set; } = [];
    /// <summary>A nudge on the pill after this long at the screen without a five-minute break.</summary>
    public bool BreakReminder { get; set; } = true;
    public int BreakEveryMinutes { get; set; } = 50;
    // Today's screen time, kept here so a restart doesn't lose it.
    public string ScreenTimeDay { get; set; } = "";
    public int ScreenTimeSeconds { get; set; }
    public Dictionary<string, int> ScreenTimeApps { get; set; } = [];

    // Shelf and activities.
    public List<Services.SavedShelfFile> ShelfFiles { get; set; } = [];
    /// <summary>Screenshots taken with the hotkey are kept on the file shelf.</summary>
    public bool CapturesToShelf { get; set; } = true;
    /// <summary>Screen recordings include what the PC is playing.</summary>
    public bool RecordSound { get; set; } = true;
    /// <summary>Screen recordings include the microphone, mixed with the PC's sound.</summary>
    public bool RecordMicrophone { get; set; }
    public bool ShowDownloadActivity { get; set; } = true;
    public bool ShowCallActivity { get; set; } = true;

    // Tools and integrations.
    public bool ObsEnabled { get; set; }
    /// <summary>Read OBS's websocket port and password from its own settings on this PC.</summary>
    public bool ObsAuto { get; set; } = true;
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";
    public bool AgentsEnabled { get; set; } = true;
    /// <summary>A notice when an agent finishes or needs you.</summary>
    public bool AgentsNotify { get; set; } = true;
    /// <summary>Launcher slots as "Name|AppId".</summary>
    public List<string> LaunchApps { get; set; } = [];

    public bool PhoneShare { get; set; } = true;
    public bool CameraEnabled { get; set; } = true;
    public string ChatTwitch { get; set; } = "";
    /// <summary>New chat messages pop up on the pill while the chat panel is closed.</summary>
    public bool ChatPopups { get; set; } = true;
    public List<string> Snippets { get; set; } = [];

    // Which panels the open island shows.
    public bool ChatPanel { get; set; }
    public bool LauncherPanel { get; set; } = true;
    public bool WebPanel { get; set; } = true;
    /// <summary>Sites on the web panel as "Name|https://…".</summary>
    public List<string> WebSites { get; set; } =
    [
        "YT Music|https://music.youtube.com/", "Spotify|https://open.spotify.com/", "ChatGPT|https://chatgpt.com/",
        "Claude|https://claude.ai/", "Gemini|https://gemini.google.com/", "WhatsApp|https://web.whatsapp.com/",
    ];
    public bool RemindersPanel { get; set; } = true;
    public bool NotificationsPanel { get; set; } = true;

    // Island shape and placement.
    /// <summary>"left", "center" or "right" along the top edge.</summary>
    public string Position { get; set; } = "center";
    public int EdgeMargin { get; set; } = 24;
    public int TopGap { get; set; } = 6;
    /// <summary>Percent: the pill, the card and their text scale together.</summary>
    public int Size { get; set; } = 100;
    /// <summary>Percent: 0 is square, 100 fully round.</summary>
    public int CornerRoundness { get; set; } = 100;
    /// <summary>"island" floats below the edge; "notch" hangs from it and curves into it.</summary>
    public string Shape { get; set; } = "island";
    /// <summary>Nothing at the top until the pointer rests on the top edge.</summary>
    public bool HiddenUntilHover { get; set; }
    /// <summary>Collapse to a thin line while a maximised window is in front, so it never covers tabs.</summary>
    public bool SlimWhenCovered { get; set; } = true;
    /// <summary>"relaxed", "normal" or "quick".</summary>
    public string AnimationSpeed { get; set; } = "normal";
    /// <summary>Per-app behaviour while that app is in front.</summary>
    public List<AppRule> AppRules { get; set; } = [];
    /// <summary>A strip with CPU, GPU, memory and time while a game is full screen.</summary>
    public bool GameStrip { get; set; } = true;
    /// <summary>Shortcut per action id ("Ctrl+Alt+I"); no entry means the default, "" means none.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    // Look, motion and accessibility.
    /// <summary>0–100: how frosty the see-through surfaces are (Acrylic, Frost, Candy, Glass lens).</summary>
    public int GlassBlur { get; set; } = 35;
    /// <summary>Percent: the open card's size (more room for the panels), apart from Size.</summary>
    public int OpenSize { get; set; } = 100;
    /// <summary>"fluid" (screen's full refresh), "balanced" (60 fps on battery) or "saver" (60 fps always).</summary>
    public string Motion { get; set; } = "fluid";
    /// <summary>"auto" follows Windows' Animation effects; "on" or "off".</summary>
    public string ReduceMotion { get; set; } = "auto";
    /// <summary>"auto" follows Windows' Text size; otherwise a percent for the Settings list.</summary>
    public string TextSize { get; set; } = "auto";
    /// <summary>"auto" follows a Windows contrast theme; "on" or "off".</summary>
    public string HigherContrast { get; set; } = "auto";
    /// <summary>Notices and read-outs stay twice as long.</summary>
    public bool LongerNotices { get; set; }
    /// <summary>"auto" (the surface's own; on Glass lens light or dark by what's behind), "white", "black", or "#RRGGBB".</summary>
    public string TextColour { get; set; } = "auto";
    /// <summary>A soft shadow under text: "auto" on see-through surfaces, "on" or "off".</summary>
    public string TextShadow { get; set; } = "auto";

    // The island.
    /// <summary>The closed island shows the song's title beside the cover while music plays.</summary>
    public bool TitleInPill { get; set; } = true;
    /// <summary>"main" opens on music or Home; "last" on the tab open when it closed.</summary>
    public string OpenOn { get; set; } = "main";
    public string LastPanel { get; set; } = "";
    /// <summary>"system", "12" or "24".</summary>
    public string TimeFormat { get; set; } = "system";
    public bool ControlsPanel { get; set; } = true;
    /// <summary>The Files tab and dropping files on the island.</summary>
    public bool ShelfEnabled { get; set; } = true;

    // Stats and the game strip.
    public bool NetworkInBits { get; set; }
    public bool StatsFahrenheit { get; set; }
    /// <summary>The strip stays up all the time, not only over games; hovering it opens the card.</summary>
    public bool GameStripEverywhere { get; set; }
    public bool StripCpu { get; set; } = true;
    public bool StripGpu { get; set; } = true;
    public bool StripRam { get; set; } = true;
    public bool StripNetwork { get; set; } = true;
    public bool StripClock { get; set; } = true;
    /// <summary>Small usage bars beside CPU and GPU.</summary>
    public bool StripBars { get; set; } = true;

    // Clipboard.
    /// <summary>Unpinned entries older than this many hours go; 0 keeps them until the list is full.</summary>
    public int ClipboardKeepHours { get; set; }
    /// <summary>The Clipboard tab as a compact list rather than cards.</summary>
    public bool ClipboardList { get; set; }

    // Chat overlay: the chat in a window of its own on the screen.
    public bool ChatOverlay { get; set; }
    public string ChatBackground { get; set; } = "#0B0B0C";
    public int ChatOpacity { get; set; } = 92;
    public string ChatTextColor { get; set; } = "#F5F5F7";
    public int ChatTextSize { get; set; } = 14;
    /// <summary>"x,y,width,height" in screen pixels; "" until it's placed.</summary>
    public string ChatBounds { get; set; } = "";

    // Everything else.
    /// <summary>"repeat" chimes every 15 s for up to a minute until answered, "once", or "off".</summary>
    public string ReminderSound { get; set; } = "repeat";
    /// <summary>The local API (notify, panel, snapshot…) on 127.0.0.1.</summary>
    public bool NotifyEndpoint { get; set; } = true;
    /// <summary>The phone page shows what you last copied.</summary>
    public bool PhoneShareCopiedText { get; set; } = true;
    /// <summary>Folders watched for finished exports and renders.</summary>
    public List<string> ExportFolders { get; set; } = [];
    /// <summary>Say so when a colour-work app is in front with Night light on.</summary>
    public bool ColorWorkWarning { get; set; } = true;
    /// <summary>A photo or video dropped on the shelf shows its resolution, length and size.</summary>
    public bool MediaInfoOnDrop { get; set; } = true;
    /// <summary>Apps whose camera or microphone use never lights the privacy dots.</summary>
    public List<string> PrivacyIgnoredApps { get; set; } = [];
    /// <summary>The web drawer stays open like a window (clicking elsewhere doesn't hide it); move and resize it.
    /// On unless switched off in the drawer's bar.</summary>
    public bool WebDrawerKeepOpen { get; set; } = true;
    /// <summary>"x,y,width,height" in screen pixels, where the kept-open drawer was left (before windows per site).</summary>
    public string WebDrawerBounds { get; set; } = "";
    /// <summary>Each site's window, where it was left: site address → "x,y,width,height" in screen pixels.</summary>
    public Dictionary<string, string> WebWindowBounds { get; set; } = [];
    /// <summary>Draw the island, widgets and windows on the graphics card (Direct3D 12) instead of the CPU; applies after a restart.</summary>
    public bool GpuDrawing { get; set; } = true;
    /// <summary>Light in the page around a playing video, in its colours.</summary>
    public bool WebGlow { get; set; } = true;
    /// <summary>Block ads and trackers in the web drawer (YouTube and YouTube Music included).</summary>
    public bool WebAdBlock { get; set; } = true;
    /// <summary>An ad that still plays in the drawer is muted, sped up and skipped.</summary>
    public bool WebAdFastForward { get; set; } = true;
    /// <summary>Unpacked browser extensions (folders with a manifest.json) for the web drawer.</summary>
    public List<string> WebExtensions { get; set; } = [];
    /// <summary>Warns when the CPU or GPU runs hot, memory is nearly full, or the Windows drive is nearly full.</summary>
    public bool PcHealthAlerts { get; set; } = true;
    /// <summary>Checks the internet every 20 seconds and says when it drops or comes back.</summary>
    public bool NetworkWatch { get; set; } = true;
    /// <summary>A reminder to unplug when charging reaches this percent; 0 for none.</summary>
    public int ChargeLimit { get; set; } = 80;
    /// <summary>Sites the web windows won't open while the focus timer runs (comma-separated).</summary>
    public string FocusBlockedSites { get; set; } = "instagram.com, facebook.com, x.com, reddit.com";
    /// <summary>Focus minutes finished today, and which day that was ("yyyy-MM-dd").</summary>
    public int FocusMinutesToday { get; set; }
    public string FocusDay { get; set; } = "";
    /// <summary>Saved window arrangements, restored from the Apps panel.</summary>
    public List<Services.WindowLayout> WindowLayouts { get; set; } = [];
    /// <summary>Saved Work, Gaming and Night profiles.</summary>
    public List<Services.Profile> Profiles { get; set; } = [];
    /// <summary>The profile last switched to or saved, or "" for none.</summary>
    public string ActiveProfile { get; set; } = "";

    private static readonly object SaveGate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize(text, SettingsJson.Default.AppSettings) ?? new();
                // Before ShowInSharing there were two options; "always" shown in sharing carries over.
                if (!text.Contains("\"ShowInSharing\"", StringComparison.Ordinal))
                    settings.ShowInSharing = text.Contains("\"ShowWhenSharing\": \"always\"", StringComparison.Ordinal);
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

    /// <summary>Writes the settings. Safe to call from any thread; writes go through a temp file.</summary>
    public void Save()
    {
        lock (SaveGate)
        {
            try
            {
                var json = JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings);
                File.WriteAllText(FilePath + ".tmp", json);
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Write($"Settings not saved: {ex.Message}");
            }
        }
    }
}

/// <summary>A Home tile pinned to the desktop: which, where (top-left, screen pixels) and how wide.</summary>
internal sealed class DesktopWidgetPlace
{
    public string Tile { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public bool Wide { get; set; }
}

/// <summary>What the island does while an app is in front: "hide", "slim" or "normal".</summary>
internal sealed class AppRule
{
    public string Process { get; set; } = "";
    public string Behaviour { get; set; } = "slim";
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;
