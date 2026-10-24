using HaloBar.Platform;
using SkiaSharp;

namespace HaloBar.Services;

internal sealed record InstalledApp(string Name, string Id)
{
    /// <summary>The shell path that launches the app and gives its icon.</summary>
    public string ShellPath => $"shell:AppsFolder\\{Id}";
}

/// <summary>
/// Six launcher slots for favourite apps, chosen from everything in the Start menu (desktop and
/// Store apps alike, through the shell's Applications folder). Icons are fetched once and cached.
/// </summary>
internal sealed class LauncherService(AppSettings settings)
{
    public const int Slots = 6;
    private readonly Dictionary<string, SKImage?> _icons = [];
    private readonly object _gate = new();
    private IReadOnlyList<InstalledApp>? _apps;
    private bool _listing;

    public event Action? Changed;

    /// <summary>The app in each slot, or null for an empty one.</summary>
    public InstalledApp?[] Slotted
    {
        get
        {
            while (settings.LaunchApps.Count < Slots) settings.LaunchApps.Add("");
            return [.. settings.LaunchApps.Take(Slots).Select(s =>
            {
                var bar = s.IndexOf('|');
                return bar > 0 ? new InstalledApp(s[..bar], s[(bar + 1)..]) : null;
            })];
        }
    }

    public void Assign(int slot, InstalledApp? app)
    {
        _ = Slotted;
        settings.LaunchApps[slot] = app == null ? "" : $"{app.Name}|{app.Id}";
        settings.Save();
        Changed?.Invoke();
    }

    /// <summary>All Start-menu apps, listed once on a worker STA thread; null until ready.</summary>
    public IReadOnlyList<InstalledApp>? Apps
    {
        get
        {
            if (_apps != null || _listing) return _apps;
            _listing = true;
            var thread = new Thread(() =>
            {
                _apps = List();
                _listing = false;
                Changed?.Invoke();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); // the Shell.Application object wants STA
            thread.Start();
            return null;
        }
    }

    private static IReadOnlyList<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return apps;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (var item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                // Uninstallers and help links clutter the list.
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" help", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(new InstalledApp(name, id));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher: listing apps failed: {ex.Message}");
        }
        return [.. apps.DistinctBy(a => a.Id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private readonly System.Collections.Concurrent.BlockingCollection<InstalledApp> _iconQueue = [];
    private Thread? _iconThread;

    /// <summary>The app's icon if fetched; queues it for the icon thread otherwise.</summary>
    public SKImage? Icon(InstalledApp app)
    {
        lock (_gate)
        {
            if (_icons.TryGetValue(app.Id, out var icon)) return icon;
            _icons[app.Id] = null;
            if (_iconThread == null)
            {
                // One STA thread fetches every icon in turn; the shell's image factory wants STA.
                _iconThread = new Thread(() =>
                {
                    foreach (var next in _iconQueue.GetConsumingEnumerable())
                    {
                        var image = ShellThumbnail.For(next.ShellPath, 64, iconOnly: true);
                        lock (_gate) _icons[next.Id] = image;
                        if (_iconQueue.Count == 0) Changed?.Invoke();
                    }
                }) { IsBackground = true, Name = "Launcher icons" };
                _iconThread.SetApartmentState(ApartmentState.STA);
                _iconThread.Start();
            }
        }
        _iconQueue.Add(app);
        return null;
    }
}
