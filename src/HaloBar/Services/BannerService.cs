using Microsoft.Win32;

namespace HaloBar.Services;

/// <summary>
/// Turns off Windows' own pop-up banner for apps whose notifications HaloBar shows, so each one
/// appears once, on the island. Windows keeps the switch per app as a DWORD <c>ShowBanner</c>
/// (0 = no banner; missing = banner) under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\&lt;app id&gt;</c>.
/// Notifications still land in the notification centre, which is where HaloBar reads them.
/// A desktop app's id can be a path ("{GUID}\WindowsPowerShell\v1.0\powershell.exe"); Windows
/// keeps those as nested keys, which is what the backslashes make of them here.
/// Every value changed here is remembered with what it was before, so turning the setting off
/// puts back exactly that and leaves the user's own choices alone.
/// </summary>
internal sealed class BannerService(AppSettings settings, string root = BannerService.WindowsRoot, Action? save = null)
{
    private readonly Action _save = save ?? settings.Save;
    public const string WindowsRoot = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    /// <summary>Remembered "before" value for an app that had no ShowBanner value at all.</summary>
    private const int Missing = -1;

    /// <summary>Stops the banner for <paramref name="appId"/> if the setting is on. Returns true if it changed anything.</summary>
    public bool Quiet(string? appId)
    {
        if (!settings.QuietWindowsBanners || string.IsNullOrWhiteSpace(appId)) return false;
        // Windows' own alerts (low battery, USB, updates) keep their banners.
        if (appId.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{appId}", writable: true);
            var before = key.GetValue("ShowBanner") is int v ? v : Missing;
            if (before == 0) return false; // already off, by the user or by us
            key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
            settings.QuietedBanners.TryAdd(appId, before);
            _save();
            Log.Write($"Banners: turned off Windows banners for {appId}");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            Log.Write($"Banners: couldn't change {appId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Quiets every app in <paramref name="appIds"/> (the ones already shown), for when the setting is switched on.</summary>
    public int QuietAll(IEnumerable<string?> appIds) => appIds.Distinct().Count(Quiet);

    /// <summary>Puts back every banner switch HaloBar changed, unless the user has changed it since.</summary>
    public int RestoreAll()
    {
        var restored = 0;
        foreach (var (appId, before) in settings.QuietedBanners.ToList())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{appId}", writable: true);
                if (key?.GetValue("ShowBanner") is 0)
                {
                    if (before == Missing) key.DeleteValue("ShowBanner", throwOnMissingValue: false);
                    else key.SetValue("ShowBanner", before, RegistryValueKind.DWord);
                    restored++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Write($"Banners: couldn't restore {appId}: {ex.Message}");
                continue; // keep it on the list to try again next time
            }
            settings.QuietedBanners.Remove(appId);
        }
        _save();
        if (restored > 0) Log.Write($"Banners: turned Windows banners back on for {restored} app(s)");
        return restored;
    }
}
