using Microsoft.Win32;
using SkiaSharp;

namespace HaloBar.UI;

/// <summary>How a surface moves or colours itself beyond its fixed colours.</summary>
internal enum SurfaceEffect { None, Aurora, Album }

/// <summary>Where an accent's colour comes from.</summary>
internal enum AccentSource { Fixed, Surface, Windows, Album, Rainbow }

/// <summary>
/// What the island is made of. Light surfaces bring dark text; soft themes also bring their own accent.
/// </summary>
internal sealed record SurfaceLook(string Id, string Name, SKColor Top, SKColor Bottom, SKColor Edge, SKColor Text,
    SKColor Accent, SurfaceEffect Effect = SurfaceEffect.None)
{
    public bool IsLight => Text.Red < 128;
}

internal sealed record AccentLook(string Id, string Name, SKColor Color, AccentSource Source = AccentSource.Fixed);

/// <summary>The built-in surfaces and accents.</summary>
internal static class Looks
{
    private static readonly SKColor Light = new(0xF5, 0xF5, 0xF7);

    public static readonly SurfaceLook[] Surfaces =
    [
        new("black", "Black", new(0x0B, 0x0B, 0x0C), new(0x0B, 0x0B, 0x0C), SKColors.White.WithAlpha(22), Light, new(0x7C, 0x9C, 0xFF)),
        // True black with no edge: on OLED screens the island's pixels stay off.
        new("pureblack", "Pure black", SKColors.Black, SKColors.Black, SKColors.Transparent, Light, new(0x7C, 0x9C, 0xFF)),
        new("graphite", "Graphite", new(0x2A, 0x2B, 0x30), new(0x1E, 0x1F, 0x23), SKColors.White.WithAlpha(30), Light, new(0xA8, 0xB4, 0xC8)),
        new("midnight", "Midnight", new(0x13, 0x1B, 0x33), new(0x0A, 0x0F, 0x1E), new(0x4A, 0x6A, 0xC8, 0x70), Light, new(0x6E, 0xA8, 0xFF)),
        new("forest", "Forest", new(0x12, 0x24, 0x1B), new(0x0A, 0x16, 0x10), new(0x3E, 0x9A, 0x6C, 0x70), Light, new(0x5F, 0xD3, 0x9A)),
        new("rose", "Rosé", new(0xF7, 0xE1, 0xE4), new(0xEE, 0xC9, 0xCF), new(0xE2, 0xA3, 0xAE), new(0x3A, 0x22, 0x28), new(0xD3, 0x5D, 0x74)),
        new("lavender", "Lavender", new(0xEA, 0xE4, 0xFA), new(0xD8, 0xCE, 0xF4), new(0xBC, 0xAE, 0xEA), new(0x2B, 0x23, 0x42), new(0x76, 0x5B, 0xD8)),
        new("peach", "Peach", new(0xFD, 0xE8, 0xD8), new(0xF8, 0xD2, 0xB8), new(0xEE, 0xB3, 0x8C), new(0x40, 0x2A, 0x1C), new(0xDD, 0x74, 0x36)),
        new("sakura", "Sakura", new(0xFC, 0xE8, 0xEF), new(0xF6, 0xD0, 0xDE), new(0xF0, 0xAC, 0xC5), new(0x3F, 0x21, 0x30), new(0xDE, 0x55, 0x88)),
        new("mint", "Mint", new(0xE2, 0xF6, 0xEE), new(0xC9, 0xEC, 0xDD), new(0x99, 0xD6, 0xBD), new(0x1C, 0x36, 0x2C), new(0x1E, 0x9E, 0x6E)),
        // Slowly drifting colours along the edge and through the surface.
        new("aurora", "Aurora", new(0x10, 0x10, 0x18), new(0x08, 0x08, 0x0E), SKColors.White.WithAlpha(40), Light, new(0x8A, 0xF0, 0xD0), SurfaceEffect.Aurora),
        // Takes its colours from the album art of whatever is playing.
        new("album", "Album", new(0x14, 0x14, 0x16), new(0x0B, 0x0B, 0x0C), SKColors.White.WithAlpha(30), Light, new(0x7C, 0x9C, 0xFF), SurfaceEffect.Album),
    ];

    public static readonly AccentLook[] Accents =
    [
        new("auto", "Surface's own", SKColors.Empty, AccentSource.Surface),
        new("white", "White", Light),
        new("windows", "Windows accent", SKColors.Empty, AccentSource.Windows),
        new("blue", "Blue", new(0x4C, 0x8D, 0xFF)),
        new("purple", "Purple", new(0x9B, 0x6B, 0xFF)),
        new("pink", "Pink", new(0xFF, 0x5F, 0xA2)),
        new("red", "Red", new(0xFF, 0x4D, 0x4F)),
        new("orange", "Orange", new(0xFF, 0x8A, 0x3D)),
        new("yellow", "Yellow", new(0xFF, 0xD2, 0x3F)),
        new("green", "Green", new(0x34, 0xC7, 0x59)),
        new("teal", "Teal", new(0x2E, 0xC4, 0xB6)),
        new("blush", "Blush", new(0xF7, 0xA1, 0xB0)),
        new("lilac", "Lilac", new(0xC7, 0xA6, 0xFF)),
        new("sakurapink", "Sakura pink", new(0xFF, 0xB7, 0xC9)),
        new("album", "Album", SKColors.Empty, AccentSource.Album),
        new("rainbow", "Rainbow", SKColors.Empty, AccentSource.Rainbow),
    ];

    public static SurfaceLook Surface(string? id) => Surfaces.FirstOrDefault(s => s.Id == id) ?? Surfaces[0];

    /// <summary>A built-in accent by id, or a custom one from a "#RRGGBB" value.</summary>
    public static AccentLook Accent(string? id)
    {
        if (Accents.FirstOrDefault(a => a.Id == id) is { } known) return known;
        return id != null && id.StartsWith('#') && SKColor.TryParse(id, out var c)
            ? new AccentLook(id, id.ToUpperInvariant(), c)
            : Accents[0];
    }

    /// <summary>The accent colour chosen in Windows' Personalization settings.</summary>
    public static SKColor WindowsAccent()
    {
        try
        {
            // Stored as 0xAABBGGRR.
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                return new SKColor((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch (System.Security.SecurityException) { }
        return new SKColor(0x00, 0x78, 0xD4);
    }
}
