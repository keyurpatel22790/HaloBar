using Microsoft.Win32;

namespace HaloBar.Services;

/// <summary>
/// Which apps are using the camera or microphone right now. Windows records every access in the
/// capability consent store; an entry whose LastUsedTimeStop is 0 is still in use.
/// </summary>
internal sealed class PrivacyService : IDisposable
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private readonly CancellationTokenSource _cts = new();
    private readonly string _self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "HaloBar");

    public IReadOnlyList<string> CameraApps { get; private set; } = [];
    public IReadOnlyList<string> MicrophoneApps { get; private set; } = [];
    public bool CameraInUse => CameraApps.Count > 0;
    public bool MicrophoneInUse => MicrophoneApps.Count > 0;

    /// <summary>Raised when the set of apps changes, with what started: ("camera" | "microphone", app).</summary>
    public event Action<string, string>? Started;
    public event Action? Changed;

    public void Start() => _ = Task.Run(() => PollAsync(_cts.Token));

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                var camera = InUse("webcam");
                var mic = InUse("microphone");
                var newCamera = camera.Except(CameraApps).ToList();
                var newMic = mic.Except(MicrophoneApps).ToList();
                var changed = !camera.SequenceEqual(CameraApps) || !mic.SequenceEqual(MicrophoneApps);
                CameraApps = camera;
                MicrophoneApps = mic;
                foreach (var app in newCamera) Started?.Invoke("camera", app);
                foreach (var app in newMic) Started?.Invoke("microphone", app);
                if (changed) Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Write($"Privacy: {ex.Message}");
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private List<string> InUse(string capability)
    {
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey($@"{Store}\{capability}");
        if (root == null) return [];
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key == null) continue;
            if (name == "NonPackaged")
            {
                foreach (var exe in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(exe);
                    if (IsActive(app)) apps.Add(FriendlyExe(exe));
                }
            }
            else if (IsActive(key)) apps.Add(FriendlyPackage(name));
        }
        apps.Remove(_self);
        return [.. apps];
    }

    private static bool IsActive(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0;

    /// <summary>"C:#Program Files#Zoom#bin#Zoom.exe" → "Zoom".</summary>
    private static string FriendlyExe(string key)
    {
        var path = key.Replace('#', '\\');
        try
        {
            if (File.Exists(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            }
        }
        catch (IOException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
    private static string FriendlyPackage(string family)
    {
        var name = family.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
