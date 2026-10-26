using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace HaloBar.Services;

/// <summary>
/// Small web look-ups for the Home tools, all free and keyless: MyMemory for translation,
/// dictionaryapi.dev for word meanings and Frankfurter (ECB) for exchange rates.
/// </summary>
internal static class WebTools
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HaloBar/0.4");
        return http;
    }

    // ───────────────────────────── translation ─────────────────────────────

    /// <summary>Languages the translator offers, as (code, name).</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"), ("hi", "Hindi"), ("gu", "Gujarati"), ("mr", "Marathi"), ("bn", "Bengali"), ("ta", "Tamil"),
        ("te", "Telugu"), ("kn", "Kannada"), ("pa", "Punjabi"), ("ur", "Urdu"), ("es", "Spanish"), ("fr", "French"),
        ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("ru", "Russian"), ("ar", "Arabic"), ("ja", "Japanese"),
        ("ko", "Korean"), ("zh-CN", "Chinese"),
    ];

    public static string LanguageName(string code) =>
        code == "auto" ? "Detect" : Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    /// <summary>Guesses the language of <paramref name="text"/> from its script; null for Latin text.</summary>
    public static string? DetectScript(string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case >= '઀' and <= '૿': return "gu";
                case >= 'ऀ' and <= 'ॿ': return "hi";
                case >= 'ঀ' and <= '৿': return "bn";
                case >= '਀' and <= '੿': return "pa";
                case >= '஀' and <= '௿': return "ta";
                case >= 'ఀ' and <= '౿': return "te";
                case >= 'ಀ' and <= '೿': return "kn";
                case >= '؀' and <= 'ۿ': return "ar";
                case >= 'Ѐ' and <= 'ӿ': return "ru";
                case >= '぀' and <= 'ヿ': return "ja";
                case >= '가' and <= '힯': return "ko";
                case >= '一' and <= '鿿': return "zh-CN";
            }
        }
        return null;
    }

    /// <summary>
    /// Translates <paramref name="text"/>; <paramref name="from"/> may be "auto". Returns the
    /// translation and the source language it was read as. Uses Google's free web endpoint,
    /// which detects the language itself, and MyMemory if that fails.
    /// </summary>
    public static async Task<(string Text, string From)> TranslateAsync(string text, string from, string to)
    {
        try
        {
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl={from}&tl={to}&q={Uri.EscapeDataString(text)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var sb = new System.Text.StringBuilder();
            foreach (var segment in root[0].EnumerateArray())
                if (segment[0].ValueKind == JsonValueKind.String) sb.Append(segment[0].GetString());
            var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString()! : from;
            if (sb.Length > 0) return (sb.ToString(), detected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            Log.Write($"Translate: Google failed, trying MyMemory: {ex.Message}");
        }
        if (from == "auto") from = DetectScript(text) ?? (to == "en" ? "Autodetect" : "en");
        if (from == to) return (text, from);
        return (await MyMemoryAsync(text, from, to), from);
    }

    private static async Task<string> MyMemoryAsync(string text, string from, string to)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={from}|{to}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var data = doc.RootElement.GetProperty("responseData");
        var translated = data.GetProperty("translatedText").GetString() ?? "";
        // Over the free daily limit MyMemory answers with a warning in place of the translation.
        if (translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Daily translation limit reached");
        return System.Net.WebUtility.HtmlDecode(translated);
    }

    // ───────────────────────────── dictionary ─────────────────────────────

    public sealed record Meaning(string Word, string Phonetic, string PartOfSpeech, string Definition, string? Example);

    /// <summary>The first few meanings of an English word, or none when it isn't found.</summary>
    public static async Task<IReadOnlyList<Meaning>> DefineAsync(string word)
    {
        using var response = await Http.GetAsync($"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(word.Trim().ToLowerInvariant())}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var list = new List<Meaning>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var phonetic = entry.TryGetProperty("phonetic", out var ph) ? ph.GetString() ?? "" : "";
            foreach (var meaning in entry.GetProperty("meanings").EnumerateArray())
            {
                var part = meaning.GetProperty("partOfSpeech").GetString() ?? "";
                foreach (var def in meaning.GetProperty("definitions").EnumerateArray().Take(2))
                {
                    var example = def.TryGetProperty("example", out var ex) ? ex.GetString() : null;
                    list.Add(new Meaning(entry.GetProperty("word").GetString() ?? word, phonetic, part, def.GetProperty("definition").GetString() ?? "", example));
                    if (list.Count >= 4) return list;
                }
            }
        }
        return list;
    }

    // ───────────────────────────── currency ─────────────────────────────

    public static readonly string[] Currencies =
        ["USD", "INR", "EUR", "GBP", "JPY", "CNY", "AUD", "CAD", "SGD", "CHF", "HKD", "SEK", "NZD", "ZAR", "THB", "KRW", "BRL"];

    private static readonly Dictionary<string, (DateTime At, Dictionary<string, double> Rates)> RateCache = [];

    /// <summary>How much one <paramref name="from"/> buys of each currency; cached for an hour.</summary>
    public static async Task<Dictionary<string, double>> RatesAsync(string from)
    {
        lock (RateCache)
            if (RateCache.TryGetValue(from, out var cached) && DateTime.Now - cached.At < TimeSpan.FromHours(1)) return cached.Rates;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.frankfurter.dev/v1/latest?base={from}"));
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [from] = 1 };
        foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject()) rates[p.Name] = p.Value.GetDouble();
        lock (RateCache) RateCache[from] = (DateTime.Now, rates);
        return rates;
    }

    public static string Money(double amount) =>
        amount >= 1000 ? amount.ToString("#,0", CultureInfo.InvariantCulture)
        : amount >= 1 ? amount.ToString("#,0.00", CultureInfo.InvariantCulture)
        : amount.ToString("0.####", CultureInfo.InvariantCulture);

    // ───────────────────────────── command line ─────────────────────────────

    /// <summary>Runs a console tool and returns its output, or null if it is missing, fails or takes too long.</summary>
    public static string? Run(string exe, string arguments, int timeoutMs = 5000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not installed
        }
    }
}
