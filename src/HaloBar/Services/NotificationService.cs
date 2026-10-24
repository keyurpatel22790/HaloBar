using HaloBar.Rendering;
using SkiaSharp;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HaloBar.Services;

/// <summary>One notification shown on the island. <paramref name="AppId"/> opens the app that sent it.</summary>
internal sealed record Notice(string App, string Title, string Body, SKImage? Icon, DateTime Time, string? AppId = null);

/// <summary>Recent notices, shared by the islands on every monitor. Window thread only.</summary>
internal sealed class NoticeHistory
{
    private const int Capacity = 30;
    private readonly List<Notice> _items = [];

    public IReadOnlyList<Notice> Items => _items;

    public void Add(Notice notice)
    {
        _items.Insert(0, notice);
        if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
    }

    public void Clear() => _items.Clear();
}

/// <summary>
/// Mirrors Windows toast notifications onto the island. Windows only hands notifications to
/// apps the user has allowed (Settings › Privacy › Notifications), and only to apps with
/// package identity, so when run unpackaged this reports itself unavailable and the local
/// API (<see cref="NotifyServer"/>) is the way in.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly HashSet<uint> _seen = [];
    private readonly Dictionary<string, SKImage?> _icons = [];
    private UserNotificationListener? _listener;
    private CancellationTokenSource? _cts;

    public string Status { get; private set; } = "not started";
    public event Action<Notice>? Arrived;

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Status = $"Windows says {access}";
                Log.Write($"Notifications: access {access}; use Settings › Privacy › Notifications");
                return;
            }
        }
        catch (Exception ex)
        {
            Status = "needs package identity";
            Log.Write($"Notifications: listener unavailable ({ex.GetType().Name} 0x{ex.HResult:X8}). The local API still works.");
            return;
        }

        // Existing notifications are history, not news.
        foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);
        Status = "mirroring";
        Log.Write("Notifications: mirroring Windows notifications");

        // NotificationChanged needs a background task registration; polling once a second works everywhere.
        _cts = new CancellationTokenSource();
        _ = PollAsync(_cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var list = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var n in list)
                {
                    if (!_seen.Add(n.Id)) continue;
                    var notice = await ToNoticeAsync(n);
                    if (notice != null) Arrived?.Invoke(notice);
                }
                // Forget ids Windows no longer holds, so the set doesn't grow forever.
                _seen.IntersectWith(list.Select(n => n.Id));
            }
            catch (Exception ex)
            {
                Log.Write($"Notifications poll: {ex.Message}");
            }
        }
    }

    private async Task<Notice?> ToNoticeAsync(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        if (texts.Count == 0) return null;
        var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
        return new Notice(app, texts[0], string.Join(" · ", texts.Skip(1)), await IconAsync(n), DateTime.Now, n.AppInfo?.AppUserModelId);
    }

    private async Task<SKImage?> IconAsync(UserNotification n)
    {
        var key = n.AppInfo?.AppUserModelId ?? "";
        if (_icons.TryGetValue(key, out var cached)) return cached;
        SKImage? image = null;
        try
        {
            var logo = n.AppInfo!.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            image = ImageTools.DecodeSmall(copy.ToArray(), 64);
        }
        catch (Exception ex)
        {
            Log.Write($"Notifications: no icon for {key}: {ex.Message}");
        }
        _icons[key] = image;
        return image;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
