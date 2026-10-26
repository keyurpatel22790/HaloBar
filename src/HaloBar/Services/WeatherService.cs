using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace HaloBar.Services;

internal sealed record WeatherSnapshot(string Place, double Temperature, int Code, bool IsDay, double High, double Low, bool Fahrenheit, DateTime Updated)
{
    /// <summary>Sunrise and sunset in the place's own time.</summary>
    public DateTime? Sunrise { get; init; }
    public DateTime? Sunset { get; init; }
    public double Uv { get; init; }

    public string Unit => Fahrenheit ? "°F" : "°C";

    public string Describe() => Code switch
    {
        0 => IsDay ? "Clear" : "Clear night",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "—",
    };
}

/// <summary>Air quality now: the US AQI and fine particles in µg/m³.</summary>
internal sealed record AirSnapshot(int Aqi, double Pm25, double Pm10)
{
    public string Describe() => Aqi switch
    {
        <= 50 => "Good",
        <= 100 => "Moderate",
        <= 150 => "Unhealthy for some",
        <= 200 => "Unhealthy",
        <= 300 => "Very unhealthy",
        _ => "Hazardous",
    };
}

/// <summary>
/// Current weather from Open-Meteo (free, no key). The place is a city the user typed, looked up
/// with Open-Meteo's geocoder, or the PC's location from Windows when nothing is set.
/// Refreshes every 20 minutes.
/// </summary>
internal sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(20);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeatherSnapshot? Snapshot { get; private set; }
    public AirSnapshot? Air { get; private set; }
    public string Status { get; private set; } = "Loading…";
    public event Action? Changed;

    public WeatherService(AppSettings settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HaloBar/0.4");
    }

    public void Start() => _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(Every);
        do await RefreshAsync();
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
    });

    /// <summary>Looks up <paramref name="city"/> and switches the weather to it.</summary>
    public async Task SetCityAsync(string city)
    {
        city = city.Trim();
        if (city.Length == 0)
        {
            _settings.WeatherCity = "";
            _settings.WeatherLatitude = _settings.WeatherLongitude = null;
            _settings.Save();
            await RefreshAsync();
            return;
        }
        try
        {
            Status = $"Finding {city}…";
            Changed?.Invoke();
            var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name={Uri.EscapeDataString(city)}";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                Status = $"No place called “{city}”";
                Changed?.Invoke();
                return;
            }
            var place = results[0];
            _settings.WeatherCity = place.GetProperty("name").GetString() ?? city;
            _settings.WeatherLatitude = place.GetProperty("latitude").GetDouble();
            _settings.WeatherLongitude = place.GetProperty("longitude").GetDouble();
            _settings.Save();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Status = "Couldn't look that up";
            Log.Write($"Weather: geocoding failed: {ex.Message}");
            Changed?.Invoke();
        }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var (lat, lon, place) = await LocationAsync();
            if (lat == null || lon == null)
            {
                Status = "Set a city";
                Changed?.Invoke();
                return;
            }
            var f = _settings.WeatherFahrenheit;
            var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}")
                      + "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset,uv_index_max&timezone=auto&forecast_days=1"
                      + (f ? "&temperature_unit=fahrenheit" : "");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            Snapshot = new WeatherSnapshot(
                place,
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("is_day").GetInt32() == 1,
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                f,
                DateTime.Now)
            {
                Sunrise = Time(daily, "sunrise"),
                Sunset = Time(daily, "sunset"),
                Uv = daily.TryGetProperty("uv_index_max", out var uv) && uv[0].ValueKind == JsonValueKind.Number ? uv[0].GetDouble() : 0,
            };
            Status = "";
            Air = await AirAsync(lat.Value, lon.Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Status = Snapshot == null ? "Weather unavailable" : "";
            Log.Write($"Weather refresh failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    private static DateTime? Time(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var list) && list[0].GetString() is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private async Task<AirSnapshot?> AirAsync(double lat, double lon)
    {
        try
        {
            var url = FormattableString.Invariant($"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat:0.####}&longitude={lon:0.####}")
                      + "&current=us_aqi,pm2_5,pm10&timezone=auto";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            return new AirSnapshot((int)Math.Round(current.GetProperty("us_aqi").GetDouble()),
                current.GetProperty("pm2_5").GetDouble(), current.GetProperty("pm10").GetDouble());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write($"Air quality failed: {ex.Message}");
            return Air;
        }
    }

    private async Task<(double? Lat, double? Lon, string Place)> LocationAsync()
    {
        if (_settings.WeatherLatitude is { } lat && _settings.WeatherLongitude is { } lon)
            return (lat, lon, string.IsNullOrEmpty(_settings.WeatherCity) ? "Here" : _settings.WeatherCity);
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return (null, null, "");
            var position = await new Geolocator { DesiredAccuracy = PositionAccuracy.Default }
                .GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
            return (position.Coordinate.Point.Position.Latitude, position.Coordinate.Point.Position.Longitude, "Here");
        }
        catch (Exception ex)
        {
            Log.Write($"Weather: no location from Windows: {ex.Message}");
            return (null, null, "");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
