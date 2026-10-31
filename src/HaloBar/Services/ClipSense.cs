using System.Globalization;
using System.Text.RegularExpressions;

namespace HaloBar.Services;

/// <summary>What a copied bit of text turned out to be, and what HaloBar offers for it.</summary>
/// <param name="Title">"12*34+5", "70 kg", "Link copied"…</param>
/// <param name="Detail">"= 413", "154.32 lb"…</param>
/// <param name="Copy">Text a click copies (the result), or null.</param>
/// <param name="Open">An address a click opens, or null.</param>
internal sealed record ClipInsight(string Title, string Detail, string? Copy, string? Open);

/// <summary>
/// Recognises short copied text worth a result: maths, unit conversions (with or without a target
/// unit), money in another currency, colours and links. Everything else is left alone.
/// </summary>
internal static partial class ClipSense
{
    public static async Task<ClipInsight?> LookAsync(string text, AppSettings settings)
    {
        text = text.Trim();
        if (text.Length is 0 or > 120 || text.Contains('\n')) return null;
        return Link(text) ?? Colour(text) ?? Units(text) ?? Maths(text) ?? await MoneyAsync(text, settings);
    }

    // ───────────────────────────── links ─────────────────────────────

    private static ClipInsight? Link(string text)
    {
        if (text.Contains(' ') || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return new ClipInsight("Link copied", uri.Host + " · click to open", null, uri.AbsoluteUri);
    }

    // ───────────────────────────── colours ─────────────────────────────

    [GeneratedRegex(@"^#?([0-9a-f]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex Hex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Rgb();

    private static ClipInsight? Colour(string text)
    {
        if (Hex().Match(text) is { Success: true } h && (text.StartsWith('#') || h.Groups[1].Value.Any(char.IsLetter)))
        {
            var v = Convert.ToInt32(h.Groups[1].Value, 16);
            var rgb = $"rgb({v >> 16 & 255}, {v >> 8 & 255}, {v & 255})";
            return new ClipInsight($"#{h.Groups[1].Value.ToUpperInvariant()}", rgb + " · click to copy", rgb, null);
        }
        if (Rgb().Match(text) is { Success: true } r)
        {
            int Part(int i) => Math.Clamp(int.Parse(r.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255);
            var hex = $"#{Part(1):X2}{Part(2):X2}{Part(3):X2}";
            return new ClipInsight(text, hex + " · click to copy", hex, null);
        }
        return null;
    }

    // ───────────────────────────── units ─────────────────────────────

    /// <summary>For "70 kg" with no target: the usual unit on the other side (metric ↔ imperial).</summary>
    private static readonly Dictionary<string, string> Other = new(StringComparer.OrdinalIgnoreCase)
    {
        ["km"] = "mi", ["mi"] = "km", ["m"] = "ft", ["ft"] = "m", ["cm"] = "in", ["in"] = "cm", ["mm"] = "in",
        ["kg"] = "lb", ["lb"] = "kg", ["g"] = "oz", ["oz"] = "g",
        ["°C"] = "°F", ["°F"] = "°C", ["C"] = "°F", ["F"] = "°C",
        ["l"] = "gal", ["gal"] = "l", ["ml"] = "floz",
        ["km/h"] = "mph", ["mph"] = "km/h",
    };

    [GeneratedRegex(@"^(-?[\d.,]+)\s*([^\d\s]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountAndUnit();

    private static ClipInsight? Units(string text)
    {
        if (UnitConverter.Convert(text) is { } converted) return new ClipInsight(text, converted + " · click to copy", converted, null);
        if (AmountAndUnit().Match(text) is not { Success: true } m) return null;
        var unit = m.Groups[2].Value.Replace("º", "°");
        if (!Other.TryGetValue(unit, out var target)) return null;
        var from = unit.Length == 1 && unit is "C" or "F" ? "°" + unit : unit;
        if (UnitConverter.Convert($"{m.Groups[1].Value} {from} to {target}") is not { } result) return null;
        return new ClipInsight(text, result + " · click to copy", result, null);
    }

    // ───────────────────────────── maths ─────────────────────────────

    // Dates, times, phone numbers and version numbers contain "-", "/", ":" or dots but aren't sums.
    [GeneratedRegex(@"^\+?[\d\s()-]{7,}$|^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}$|^\d{1,2}:\d{2}|^\d+(\.\d+){2,}$")]
    private static partial Regex NotMaths();

    private static ClipInsight? Maths(string text)
    {
        if (NotMaths().IsMatch(text)) return null;
        // An operator that isn't just a leading minus or a hyphen between digits.
        var hasOperator = text.IndexOfAny(['+', '*', '/', '×', '÷', '^', '%', '(']) >= 0 || text.Contains(" - ") || text.Contains("sqrt", StringComparison.OrdinalIgnoreCase);
        if (!hasOperator || !text.Any(char.IsDigit) || text.Any(char.IsLetter) && !text.Contains("sqrt", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Calculator.TryEvaluate(text, out var value)) return null;
        var result = Calculator.Format(value);
        return result == text ? null : new ClipInsight(text, "= " + result + " · click to copy", result, null);
    }

    // ───────────────────────────── money ─────────────────────────────

    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["$"] = "USD", ["€"] = "EUR", ["£"] = "GBP", ["₹"] = "INR", ["¥"] = "JPY", ["A$"] = "AUD", ["C$"] = "CAD", ["S$"] = "SGD",
    };

    [GeneratedRegex(@"^(A\$|C\$|S\$|[$€£₹¥])\s*([\d.,]+)\s*(k|m)?$|^([\d.,]+)\s*(k|m)?\s*([A-Za-z]{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyText();

    private static async Task<ClipInsight?> MoneyAsync(string text, AppSettings settings)
    {
        if (MoneyText().Match(text) is not { Success: true } m) return null;
        string from, amountText, scale;
        if (m.Groups[1].Success) (from, amountText, scale) = (Symbols[m.Groups[1].Value], m.Groups[2].Value, m.Groups[3].Value);
        else (from, amountText, scale) = (m.Groups[6].Value.ToUpperInvariant(), m.Groups[4].Value, m.Groups[5].Value);
        if (!WebTools.Currencies.Contains(from)) return null;
        if (!double.TryParse(amountText.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return null;
        amount *= scale.ToLowerInvariant() switch { "k" => 1e3, "m" => 1e6, _ => 1 };
        // The first of the person's currencies that isn't the one copied.
        var to = settings.CurrencyTargets.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Prepend(settings.CurrencyBase).Select(c => c.ToUpperInvariant()).FirstOrDefault(c => c != from && WebTools.Currencies.Contains(c));
        if (to == null) return null;
        try
        {
            var rates = await WebTools.RatesAsync(from);
            if (!rates.TryGetValue(to, out var rate)) return null;
            var result = $"{WebTools.Money(amount * rate)} {to}";
            return new ClipInsight(text, "≈ " + result + " · click to copy", result, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
