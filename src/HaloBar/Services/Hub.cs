using HaloBar.UI;

namespace HaloBar.Services;

/// <summary>
/// Everything the islands' panels read from and act on, shared by every monitor's island.
/// Built once by the app; panels hold a reference and keep only their own view state.
/// </summary>
internal sealed class Hub
{
    public required AppSettings Settings { get; init; }
    public required Theme Theme { get; init; }
    public required MediaService Media { get; init; }
    public required VolumeService Volume { get; init; }
    public required MicrophoneService Microphone { get; init; }
    public required BrightnessService Brightness { get; init; }
    public required PowerService Power { get; init; }
    public required PrivacyService Privacy { get; init; }
    public required BluetoothService Bluetooth { get; init; }
    public required ClipboardService Clipboard { get; init; }
    public required NoticeHistory Notices { get; init; }
    public required NotificationService Notifications { get; init; }
    public required WeatherService Weather { get; init; }
    public required StatsService Stats { get; init; }
    public required TimerService Timer { get; init; }
    public required ReminderService Reminders { get; init; }
    public required CalendarService Calendar { get; init; }
    public required LyricsService Lyrics { get; init; }
    public required ShelfService Shelf { get; init; }
    public required DownloadService Downloads { get; init; }
    public required TickerService Ticker { get; init; }
    public required ControlsService Controls { get; init; }
    public required AgentService Agents { get; init; }
    public required LauncherService Launcher { get; init; }
    public required ObsService Obs { get; init; }
    public required PhoneShareService Phone { get; init; }
    public required ChatService Chat { get; init; }
    public required CameraService Camera { get; init; }
    public required ScreenTimeService ScreenTime { get; init; }
    public required BannerService Banners { get; init; }
    public required HotkeyService Hotkeys { get; init; }
    public required Platform.WebDrawer Web { get; init; }
    public required ScreenRecorder Recorder { get; init; }
    /// <summary>Starts recording the screen the pointer is on, or stops and saves.</summary>
    public required Action ToggleRecording { get; init; }
    public required int ApiPort { get; init; }
    /// <summary>The process name of the app in front, kept current by the frame loop.</summary>
    public string FrontApp { get; set; } = "";

    /// <summary>Shows a HUD on every island.</summary>
    public required Action<Hud> ShowHud { get; init; }
    /// <summary>Opens a link or file with its default app.</summary>
    public required Action<string> Open { get; init; }
    /// <summary>Shows a file selected in Explorer.</summary>
    public required Action<string> Reveal { get; init; }
}
