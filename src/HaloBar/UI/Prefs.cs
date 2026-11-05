using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HaloBar.UI;

/// <summary>
/// Display preferences read all over the drawing code (clocks, speeds, temperatures, contrast,
/// motion). They are copied from <see cref="AppSettings"/> by <see cref="Apply"/>, at startup and
/// whenever Settings changes one, so the drawing code needn't carry the settings around.
/// "auto" choices follow Windows' own accessibility settings.
/// </summary>
internal static class Prefs
{
    /// <summary>Springs settle without overshooting (Reduce motion).</summary>
    public static bool NoBounce { get; private set; }
    /// <summary>Secondary text is drawn brighter (Higher contrast).</summary>
    public static bool HighContrast { get; private set; }
    /// <summary>Network speeds in megabits a second rather than megabytes.</summary>
    public static bool Bits { get; private set; }
    public static bool Fahrenheit { get; private set; }
    /// <summary>"system", "12" or "24".</summary>
    public static string TimeFormat { get; private set; } = "system";
    /// <summary>0–1: how frosty see-through surfaces are; 0.5 is the usual look.</summary>
    public static float GlassFrost { get; private set; } = 0.5f;
    /// <summary>The Glass lens in its classic look (smoked, always dark) rather than the liquid one.</summary>
    public static bool GlassClassic { get; private set; }
    /// <summary>"auto", "white", "black" or "#RRGGBB".</summary>
    public static string TextColour { get; private set; } = "auto";
    /// <summary>"auto" (see-through surfaces), "on" or "off".</summary>
    public static string TextShadow { get; private set; } = "auto";
    /// <summary>Text size in Settings, 1 = as drawn.</summary>
    public static float TextScale { get; private set; } = 1;

    public static void Apply(AppSettings s)
    {
        NoBounce = s.ReduceMotion switch { "on" => true, "off" => false, _ => !WindowsAnimates() };
        HighContrast = s.HigherContrast switch { "on" => true, "off" => false, _ => WindowsHighContrast() };
        Bits = s.NetworkInBits;
        GlassFrost = Math.Clamp(s.GlassBlur, 0, 100) / 100f;
        GlassClassic = s.GlassStyle == "classic";
        Fahrenheit = s.StatsFahrenheit;
        TimeFormat = s.TimeFormat;
        TextColour = s.TextColour;
        TextShadow = s.TextShadow;
        TextScale = s.TextSize == "auto" ? WindowsTextScale() : Math.Clamp(int.TryParse(s.TextSize, out var p) ? p : 100, 80, 160) / 100f;
    }

    /// <summary>A time of day in the chosen format: "14:30" or "2:30 pm".</summary>
    public static string Time(DateTime t) => TimeFormat switch
    {
        "24" => t.ToString("HH:mm"),
        "12" => t.ToString("h:mm tt").ToLowerInvariant(),
        _ => t.ToShortTimeString(),
    };

    /// <summary>A temperature given in °C, in the chosen unit: "54°".</summary>
    public static string Temp(double celsius) => $"{(Fahrenheit ? celsius * 9 / 5 + 32 : celsius):0}°";

    // ── What Windows says ──

    private const uint SPI_GETHIGHCONTRAST = 0x42, SPI_GETCLIENTAREAANIMATION = 0x1042, HCF_HIGHCONTRASTON = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST { public int cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    /// <summary>Windows' "Animation effects" switch.</summary>
    private static bool WindowsAnimates() => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on != 0;

    /// <summary>A Windows contrast theme is on.</summary>
    private static bool WindowsHighContrast()
    {
        var hc = new HIGHCONTRAST { cbSize = Marshal.SizeOf<HIGHCONTRAST>() };
        return SystemParametersInfo(SPI_GETHIGHCONTRAST, (uint)hc.cbSize, ref hc, 0) && (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    /// <summary>Windows' Accessibility › Text size, as a factor (1 to 2.25).</summary>
    private static float WindowsTextScale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent ? Math.Clamp(percent, 100, 225) / 100f : 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}
