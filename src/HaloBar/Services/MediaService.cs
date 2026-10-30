using HaloBar.Rendering;
using SkiaSharp;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace HaloBar.Services;

/// <summary>What is playing right now, as Windows reports it through its media controls.</summary>
internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    string SourceApp,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionTakenAt,
    SKImage? Artwork,
    ArtPalette? Palette,
    bool CanPrevious,
    bool CanNext,
    bool CanSeek)
{
    /// <summary>Position now, moved on from the last report while playing.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var p = Position + (DateTimeOffset.Now - PositionTakenAt);
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }
}

/// <summary>
/// Follows the system media session (Spotify, browsers, Media Player…) and exposes
/// play/pause/skip/seek. All WinRT callbacks arrive on thread-pool threads; the
/// latest state is published as an immutable snapshot.
/// </summary>
internal sealed class MediaService(AppSettings settings) : IDisposable
{
    /// <summary>Artwork is drawn at most 120 DIPs; at 2x scaling that needs 240 pixels.</summary>
    private const int ArtworkSize = 256;

    private MediaManager? _manager;
    private MediaSession? _session;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _artworkKey = "";
    private SKImage? _artwork;
    private ArtPalette? _palette;

    public MediaSnapshot? Snapshot { get; private set; }
    public event Action? Changed;

    /// <summary>Apps with a media session right now, as (app id, name).</summary>
    public IReadOnlyList<(string Id, string Name)> Sources
    {
        get
        {
            try
            {
                return _manager == null ? [] : [.. _manager.GetSessions().Select(s => (s.SourceAppUserModelId, FriendlyAppName(s.SourceAppUserModelId))).Distinct()];
            }
            catch (Exception) { return []; }
        }
    }

    /// <summary>Shows only <paramref name="id"/>'s music from now on, or any app's when it is "".</summary>
    public void Choose(string id)
    {
        settings.MusicSource = id;
        settings.Save();
        Reattach();
    }

    /// <summary>Picks again after the source settings change.</summary>
    public void Refresh() => Reattach();

    public async Task StartAsync()
    {
        try
        {
            _manager = await MediaManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Reattach();
            _manager.SessionsChanged += (_, _) => Reattach();
            Reattach();
        }
        catch (Exception ex)
        {
            Log.Write($"Media: unavailable: {ex.Message}");
        }
    }

    private void Reattach()
    {
        var chosen = ChooseSession();
        if (!ReferenceEquals(chosen, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _session = chosen;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
                _session.TimelinePropertiesChanged += OnSessionChanged;
            }
        }
        _ = RefreshAsync();
    }

    /// <summary>
    /// The chosen app's session if one is chosen and open; otherwise the current session, unless it
    /// is paused while another one plays. Apps switched off in Settings are never shown.
    /// </summary>
    private MediaSession? ChooseSession()
    {
        if (_manager == null) return null;
        try
        {
            var sessions = _manager.GetSessions().ToList();
            Remember(sessions);
            var allowed = sessions.Where(s => !settings.MusicHidden.Contains(s.SourceAppUserModelId)).ToList();
            if (settings.MusicSource.Length > 0 && allowed.FirstOrDefault(s => s.SourceAppUserModelId == settings.MusicSource) is { } pinned)
                return pinned;
            var current = _manager.GetCurrentSession();
            if (current != null && allowed.Any(s => s.SourceAppUserModelId == current.SourceAppUserModelId))
            {
                if (IsPlaying(current)) return current;
            }
            else current = null;
            var playing = allowed.FirstOrDefault(IsPlaying);
            return playing ?? current ?? allowed.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Write($"Media: listing sessions failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps a list of every app seen playing, for the switches in Settings.</summary>
    private void Remember(IEnumerable<MediaSession> sessions)
    {
        var added = false;
        foreach (var s in sessions)
            added |= settings.MusicSeen.TryAdd(s.SourceAppUserModelId, FriendlyAppName(s.SourceAppUserModelId));
        if (added) settings.Save();
    }

    private static bool IsPlaying(MediaSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private void OnSessionChanged(MediaSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var session = _session;
            if (session == null)
            {
                Publish(null);
                return;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";
            if (string.IsNullOrWhiteSpace(artist)) artist = props?.AlbumArtist ?? "";

            var key = $"{session.SourceAppUserModelId}|{title}|{artist}|{props?.AlbumTitle}";
            if (key != _artworkKey)
            {
                _artworkKey = key;
                _artwork = props?.Thumbnail == null ? null : await ReadArtworkAsync(props.Thumbnail);
                _palette = ArtPalette.From(_artwork);
            }

            var controls = playback?.Controls;
            Publish(new MediaSnapshot(
                title,
                artist,
                FriendlyAppName(session.SourceAppUserModelId),
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position,
                timeline.EndTime - timeline.StartTime,
                timeline.LastUpdatedTime,
                _artwork,
                _palette,
                controls?.IsPreviousEnabled ?? false,
                controls?.IsNextEnabled ?? false,
                controls?.IsPlaybackPositionEnabled ?? false));
        }
        catch (Exception ex)
        {
            // Players sometimes vanish mid-call (RPC_E_DISCONNECTED and friends); the next event retries.
            Log.Write($"Media refresh: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<SKImage?> ReadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            return ImageTools.DecodeSmall(copy.ToArray(), ArtworkSize);
        }
        catch (Exception ex)
        {
            Log.Write($"Media artwork: {ex.Message}");
            return null;
        }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke();
    }

    private static string FriendlyAppName(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("zunemusic") || id.Contains("media")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";
        var name = appId.Split('!', '\\').Last();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public void TogglePlayPause() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public void Next() => Run(s => s.TrySkipNextAsync().AsTask());
    public void Previous() => Run(s => s.TrySkipPreviousAsync().AsTask());
    public void Seek(TimeSpan position) => Run(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    private void Run(Func<MediaSession, Task<bool>> command)
    {
        var session = _session;
        if (session == null) return;
        Task.Run(async () =>
        {
            try { await command(session); }
            catch (Exception ex) { Log.Write($"Media command: {ex.Message}"); }
        });
    }

    public void Dispose() => _refreshGate.Dispose();
}
