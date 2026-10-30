using System.Net.Http;
using System.Text.Json;

namespace HaloBar.Services;

internal sealed record Quote(string Symbol, double Price, double ChangePercent, string Currency);

/// <summary>"BTC above 70000": a level to watch a price cross.</summary>
internal sealed record PriceAlert(string Symbol, bool Above, double Level)
{
    public override string ToString() => $"{Symbol} {(Above ? ">" : "<")} {Level.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>"BTC > 70000; AAPL < 180" (also "above"/"below", and ","/newlines between alerts).</summary>
    public static List<PriceAlert> Parse(string text)
    {
        var alerts = new List<PriceAlert>();
        foreach (var part in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(part,
                @"^\s*([\^A-Za-z0-9.\-=]+)\s*(>=?|<=?|above|over|below|under)\s*\$?([\d,]+(?:\.\d+)?)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[3].Value.Replace(",", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level)) continue;
            var above = m.Groups[2].Value.ToLowerInvariant() is ">" or ">=" or "above" or "over";
            alerts.Add(new PriceAlert(m.Groups[1].Value.ToUpperInvariant(), above, level));
        }
        return alerts;
    }

    public bool Met(double price) => Above ? price >= Level : price <= Level;
}

/// <summary>
/// Prices for a few symbols: crypto from CoinGecko, stocks and indices from Yahoo Finance's chart
/// endpoint. Refreshes every five minutes while the ticker tile is on screen, and also while any
/// price alert is set, so an alert fires with the tile out of sight.
/// </summary>
internal sealed class TickerService : IDisposable
{
    private static readonly Dictionary<string, string> Coins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "bitcoin", ["ETH"] = "ethereum", ["SOL"] = "solana", ["XRP"] = "ripple", ["DOGE"] = "dogecoin",
        ["ADA"] = "cardano", ["BNB"] = "binancecoin", ["LTC"] = "litecoin", ["DOT"] = "polkadot", ["TON"] = "the-open-network",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _wantedAt, _fetchedAt;
    private string _fetchedFor = "";

    public IReadOnlyList<Quote> Quotes { get; private set; } = [];
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public TickerService(AppSettings settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 HaloBar/0.5");
    }

    public string[] Symbols => _settings.TickerSymbols.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim().ToUpperInvariant()).Take(4).ToArray();

    /// <summary>Call while the tile is on screen.</summary>
    public void Touch() => _wantedAt = DateTime.UtcNow;

    /// <summary>Raised once each time a price crosses an alert's level (again only after it crosses back).</summary>
    public event Action<PriceAlert, Quote>? AlertTriggered;
    private readonly HashSet<string> _fired = [];

    public List<PriceAlert> Alerts => PriceAlert.Parse(_settings.PriceAlerts);

    public void SetAlerts(string text)
    {
        _settings.PriceAlerts = string.Join("; ", PriceAlert.Parse(text));
        _settings.Save();
        _fired.Clear();
        _fetchedAt = default;
    }

    public void SetSymbols(string text)
    {
        _settings.TickerSymbols = text.Trim();
        _settings.Save();
        _fetchedAt = default;
    }

    public void Start() => _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
        {
            var wanted = DateTime.UtcNow - _wantedAt < TimeSpan.FromSeconds(10) || Alerts.Count > 0;
            var key = string.Join(",", Symbols) + "|" + _settings.PriceAlerts;
            if (!wanted || (key == _fetchedFor && DateTime.UtcNow - _fetchedAt < TimeSpan.FromMinutes(5))) continue;
            await RefreshAsync();
            _fetchedFor = key;
            _fetchedAt = DateTime.UtcNow;
        }
    });

    private async Task RefreshAsync()
    {
        var shown = Symbols;
        var alerts = Alerts;
        var symbols = shown.Concat(alerts.Select(a => a.Symbol)).Distinct().ToArray();
        if (symbols.Length == 0)
        {
            Quotes = [];
            Status = "Add symbols";
            Changed?.Invoke();
            return;
        }
        var quotes = new List<Quote>();
        var crypto = symbols.Where(Coins.ContainsKey).ToList();
        try
        {
            if (crypto.Count > 0)
            {
                var ids = string.Join(",", crypto.Select(s => Coins[s]));
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                    $"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true"));
                foreach (var s in crypto)
                    if (doc.RootElement.TryGetProperty(Coins[s], out var coin))
                        quotes.Add(new Quote(s, coin.GetProperty("usd").GetDouble(),
                            coin.TryGetProperty("usd_24h_change", out var ch) ? ch.GetDouble() : 0, "USD"));
            }
            // One at a time: a symbol Yahoo doesn't know shouldn't take the others' prices with it.
            foreach (var s in symbols.Except(crypto))
            {
                if (await QuoteAsync(_resolved.GetValueOrDefault(s, s)) is { } quote) quotes.Add(quote with { Symbol = s });
                else if (!_resolved.ContainsKey(s) && await SearchAsync(s) is { } found && found != s)
                {
                    // "JPL" → "JPL.NS", "jio" → the first listed company Yahoo finds for it.
                    _resolved[s] = found;
                    Log.Write($"Ticker: {s} is {found}");
                    if (await QuoteAsync(found) is { } q) quotes.Add(q with { Symbol = s });
                }
            }
            // Keep the order the user typed.
            Quotes = [.. shown.Select(s => quotes.FirstOrDefault(q => q.Symbol == s)).Where(q => q != null)!];
            Status = Quotes.Count == 0 && shown.Length > 0 ? "No prices found" : "";
            foreach (var alert in alerts)
            {
                if (quotes.FirstOrDefault(q => q.Symbol == alert.Symbol) is not { } quote) continue;
                var key = alert.ToString();
                if (!alert.Met(quote.Price)) _fired.Remove(key);
                else if (_fired.Add(key)) AlertTriggered?.Invoke(alert, quote);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Status = "Prices unavailable";
            Log.Write($"Ticker: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>What a typed symbol turned out to be on Yahoo ("JPL" → "JPL.NS").</summary>
    private readonly Dictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Yahoo's price for one symbol, or null when it has none.</summary>
    private async Task<Quote?> QuoteAsync(string symbol)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=1d"));
            var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
            var price = meta.GetProperty("regularMarketPrice").GetDouble();
            var previous = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble() : price;
            return new Quote(symbol, price, previous == 0 ? 0 : (price - previous) / previous * 100,
                meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Yahoo's own search: the best-matching listed symbol for a ticker or a company name.</summary>
    private async Task<string?> SearchAsync(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"https://query1.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(text)}&quotesCount=6&newsCount=0"));
            var quotes = doc.RootElement.GetProperty("quotes").EnumerateArray()
                .Where(q => q.TryGetProperty("symbol", out _)).ToList();
            // An exact ticker on some exchange ("JPL" → "JPL.NS") beats a looser name match.
            var exact = quotes.FirstOrDefault(q => q.GetProperty("symbol").GetString()!.Split('.')[0].Equals(text, StringComparison.OrdinalIgnoreCase));
            var pick = exact.ValueKind != JsonValueKind.Undefined ? exact : quotes.FirstOrDefault();
            return pick.ValueKind == JsonValueKind.Undefined ? null : pick.GetProperty("symbol").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
