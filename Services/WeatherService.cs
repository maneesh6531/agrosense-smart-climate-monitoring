using System.Text.Json;
using ClimateMonitoringSystem.Models;

namespace ClimateMonitoringSystem.Services;

public class WeatherService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public WeatherService(HttpClient http) => _http = http;

    public async Task<WeatherResponse> GetWeatherAsync(double latitude, double longitude)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m" +
            "&hourly=temperature_2m,relative_humidity_2m,precipitation_probability,precipitation" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,weather_code,uv_index_max" +
            "&forecast_days=7&timezone=auto";
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<WeatherResponse>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Weather service returned an empty response.");
    }

    public async Task<List<PlaceResult>> SearchPlacesAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=8&language=en&format=json";
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GeocodingResponse>(json, _jsonOptions)?.Results ?? [];
    }

    public static string DescribeWeather(int code) => code switch
    {
        0 => "Clear sky", 1 => "Mainly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", 51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain", 66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow", 80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers", 95 or 96 or 99 => "Thunderstorm", _ => "Unknown"
    };

    public static string MakeRecommendation(CurrentWeather? c)
    {
        if (c is null) return "Weather data is currently unavailable. Check again later.";
        if (c.Precipitation >= 2 || c.Humidity >= 85)
            return "Recent precipitation or high humidity is present. Consider postponing irrigation and inspect field soil moisture before watering.";
        if (c.Temperature >= 35)
            return "High temperature detected. Check crop water stress, consider irrigation during cooler hours, and monitor plants for heat stress.";
        if (c.Temperature >= 30)
            return "Warm conditions detected. Monitor soil moisture and plan irrigation according to crop stage and actual field conditions.";
        if (c.WindSpeed >= 30)
            return "Wind speed is elevated. Avoid spraying during strong winds and inspect crops and support structures.";
        return "Conditions appear moderate. Continue routine field monitoring and base irrigation on measured soil moisture and crop requirements.";
    }
}
