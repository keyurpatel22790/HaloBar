using System.Net.Http;
using System.Text.Json;

namespace HaloBar.Services;

internal sealed record Quote(string Symbol, double Price, double ChangePercent, string Currency);

/// <summary>
/// Prices for a few symbols: crypto from CoinGecko, stocks and indices from Yahoo Finance's chart
/// endpoint. Refreshes every five minutes while the ticker tile is on screen.
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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 HaloBar/0.4");
    }

    public string[] Symbols => _settings.TickerSymbols.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim().ToUpperInvariant()).Take(4).ToArray();

    /// <summary>Call while the tile is on screen.</summary>
    public void Touch() => _wantedAt = DateTime.UtcNow;

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
            var wanted = DateTime.UtcNow - _wantedAt < TimeSpan.FromSeconds(10);
            var key = string.Join(",", Symbols);
            if (!wanted || (key == _fetchedFor && DateTime.UtcNow - _fetchedAt < TimeSpan.FromMinutes(5))) continue;
            await RefreshAsync();
            _fetchedFor = key;
            _fetchedAt = DateTime.UtcNow;
        }
    });

    private async Task RefreshAsync()
    {
        var symbols = Symbols;
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
            foreach (var s in symbols.Except(crypto))
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                    $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(s)}?range=1d&interval=1d"));
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                var price = meta.GetProperty("regularMarketPrice").GetDouble();
                var previous = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble() : price;
                quotes.Add(new Quote(s, price, previous == 0 ? 0 : (price - previous) / previous * 100,
                    meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : ""));
            }
            // Keep the order the user typed.
            Quotes = [.. symbols.Select(s => quotes.FirstOrDefault(q => q.Symbol == s)).Where(q => q != null)!];
            Status = Quotes.Count == 0 ? "No prices found" : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Status = "Prices unavailable";
            Log.Write($"Ticker: {ex.Message}");
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
