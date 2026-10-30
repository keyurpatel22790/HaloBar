using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloBar.UI;

/// <summary>
/// HaloBar's text in other languages. Everything drawn goes through <see cref="T"/> on its way to
/// the screen, so the code keeps writing English and the lookup happens at the last moment. Tables
/// are built into the app from Languages\*.json (English → translation); a text with numbers or
/// names in it matches a template such as "{0} days to go". Anything not in the table, like a song
/// title, is shown as it is. Window thread only.
/// </summary>
internal static class Lang
{
    /// <summary>Codes and the language's own name, in the order the setting cycles through them.</summary>
    public static readonly (string Code, string Name)[] Available =
    [
        ("auto", "Windows language"), ("en", "English"), ("ar", "العربية"), ("cs", "Čeština"), ("de", "Deutsch"), ("es", "Español"),
        ("fr", "Français"), ("gu", "ગુજરાતી"), ("hi", "हिन्दी"), ("id", "Bahasa Indonesia"), ("it", "Italiano"), ("ja", "日本語"),
        ("ko", "한국어"), ("nl", "Nederlands"), ("pl", "Polski"), ("pt-BR", "Português (Brasil)"), ("ru", "Русский"), ("th", "ไทย"),
        ("tr", "Türkçe"), ("uk", "Українська"), ("vi", "Tiếng Việt"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"),
    ];

    private const int CacheLimit = 4000, TemplateMaxLength = 160;
    private static Dictionary<string, string> _table = [];
    private static List<(Regex Pattern, string Translation)> _templates = [];
    private static readonly Dictionary<string, string> Cache = [];

    /// <summary>The language in use ("en" when nothing is translated).</summary>
    public static string Current { get; private set; } = "en";

    public static string Name(string code) => Available.FirstOrDefault(a => a.Code == code).Name ?? code;

    private static readonly CultureInfo WindowsCulture = CultureInfo.CurrentCulture;

    /// <summary>Switches to <paramref name="setting"/> ("auto" follows Windows).</summary>
    public static void Use(string setting)
    {
        var code = setting == "auto" ? FromWindows() : setting;
        // Dates and numbers follow the chosen language too ("Friday" → "શુક્રવાર"); with "auto" or
        // English they stay as Windows formats them.
        var culture = setting is "auto" or "en" ? WindowsCulture : CultureFor(code);
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = culture;
        Cache.Clear();
        _table = Load(code);
        Current = _table.Count > 0 ? code : "en";
        _templates = [.. _table.Where(p => p.Key.Contains("{0}"))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => (Template(p.Key), p.Value))];
        Log.Write($"Language: {Name(Current)} ({_table.Count} strings)");
    }

    /// <summary>The text in the current language, or as it is when there's no translation.</summary>
    public static string T(string text)
    {
        if (_table.Count == 0 || string.IsNullOrEmpty(text)) return text;
        if (Cache.TryGetValue(text, out var done)) return done;
        var result = Lookup(text);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    private static string Lookup(string text)
    {
        if (_table.TryGetValue(text, out var exact)) return exact;
        if (text.Length > TemplateMaxLength || !text.Any(char.IsLetter)) return text;
        foreach (var (pattern, translation) in _templates)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            // The holes keep what was there in English (numbers, names), translated if that is itself a known word.
            return Regex.Replace(translation, @"\{(\d+)\}", h =>
            {
                var group = m.Groups["h" + h.Groups[1].Value];
                if (!group.Success) return h.Value;
                return _table.GetValueOrDefault(group.Value, group.Value);
            });
        }
        return text;
    }

    /// <summary>"{0} days to go" → ^(?&lt;h0&gt;.+?) days to go$, one named group per hole.</summary>
    private static Regex Template(string english)
    {
        var parts = Regex.Split(english, @"\{\d+\}");
        var holes = Regex.Matches(english, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToList();
        var pattern = "^" + Regex.Escape(parts[0]);
        for (var i = 0; i < holes.Count; i++) pattern += $"(?<h{holes[i]}>.+?)" + Regex.Escape(parts[i + 1]);
        return new Regex(pattern + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static Dictionary<string, string> Load(string code)
    {
        if (code == "en") return [];
        using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"HaloBar.Languages.{code}.json");
        if (stream == null) return [];
        try
        {
            return JsonSerializer.Deserialize(stream, LangJson.Default.DictionaryStringString) ?? [];
        }
        catch (JsonException ex)
        {
            Log.Write($"Language {code}: unreadable: {ex.Message}");
            return [];
        }
    }

    /// <summary>A culture with the Gregorian calendar for each language (plain "ar" would count in Hijri).</summary>
    private static CultureInfo CultureFor(string code)
    {
        var name = code switch
        {
            "ar" => "ar-AE", "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", "gu" => "gu-IN", "hi" => "hi-IN", _ => code,
        };
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return WindowsCulture; }
    }

    /// <summary>The Windows display language as one of ours: zh-CN → zh-Hans, pt-PT → pt-BR, en-IN → en.</summary>
    private static string FromWindows()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("TW") || name.Contains("HK") || name.Contains("MO") || name.Contains("Hant") ? "zh-Hant" : "zh-Hans";
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        var two = culture.TwoLetterISOLanguageName;
        return Available.Any(a => a.Code == two) ? two : "en";
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class LangJson : System.Text.Json.Serialization.JsonSerializerContext;
