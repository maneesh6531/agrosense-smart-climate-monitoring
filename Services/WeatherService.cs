using System.Globalization;
using System.Net;
using System.Text.Json;
using ClimateMonitoringSystem.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ClimateMonitoringSystem.Services;

public class WeatherServiceException : Exception
{
    public WeatherServiceException(string message) : base(message) { }
    public WeatherServiceException(string message, Exception innerException) : base(message, innerException) { }
}

public class WeatherRateLimitException : WeatherServiceException
{
    public TimeSpan? RetryAfter { get; }
    public WeatherRateLimitException(string message, TimeSpan? retryAfter = null) : base(message)
    {
        RetryAfter = retryAfter;
    }
}

public class WeatherService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WeatherService> _logger;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WeatherService(HttpClient http, IMemoryCache cache, ILogger<WeatherService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    private static string GetWeatherCacheKey(double latitude, double longitude)
    {
        // Round to 4 decimal places (~11 meters) for deterministic caching
        var lat = Math.Round(latitude, 4).ToString("F4", CultureInfo.InvariantCulture);
        var lon = Math.Round(longitude, 4).ToString("F4", CultureInfo.InvariantCulture);
        return $"weather_{lat}_{lon}";
    }

    private static string GetGeocodingCacheKey(string name)
    {
        return $"places_{name.Trim().ToLowerInvariant()}";
    }

    public async Task<WeatherResponse> GetWeatherAsync(double latitude, double longitude)
    {
        var cacheKey = GetWeatherCacheKey(latitude, longitude);

        // 1. Check in-memory cache (10 minutes retention)
        if (_cache.TryGetValue(cacheKey, out WeatherResponse? cachedResponse) && cachedResponse != null)
        {
            _logger.LogInformation("Weather cache HIT for key: {CacheKey}", cacheKey);
            return cachedResponse;
        }

        // 2. Check if a recent global rate limit cooldown is active
        if (_cache.TryGetValue("weather_ratelimit_cooldown", out string? rateLimitMsg))
        {
            throw new WeatherRateLimitException(rateLimitMsg ?? "Weather rate limit cooldown active. Please wait a moment.");
        }

        _logger.LogInformation("Weather cache MISS for key: {CacheKey}. Fetching from Open-Meteo...", cacheKey);

        var lat = latitude.ToString(CultureInfo.InvariantCulture);
        var lon = longitude.ToString(CultureInfo.InvariantCulture);

        var url = "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m" +
            "&hourly=temperature_2m,relative_humidity_2m,precipitation_probability,precipitation" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,weather_code,uv_index_max" +
            "&forecast_days=7&timezone=auto";

        var responseJson = await ExecuteWithRetryAsync(url, "Weather Forecast");

        var weatherData = JsonSerializer.Deserialize<WeatherResponse>(responseJson, _jsonOptions);
        if (weatherData?.Current == null && weatherData?.Daily == null)
        {
            throw new WeatherServiceException("Weather service returned an incomplete response.");
        }

        // Cache successful response for 10 minutes
        _cache.Set(cacheKey, weatherData, TimeSpan.FromMinutes(10));

        return weatherData!;
    }

    public async Task<List<PlaceResult>> SearchPlacesAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [];

        var cacheKey = GetGeocodingCacheKey(name);

        // 1. Check in-memory cache (24 hours retention for geocoding)
        if (_cache.TryGetValue(cacheKey, out List<PlaceResult>? cachedPlaces) && cachedPlaces != null)
        {
            return cachedPlaces;
        }

        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language=en&format=json";

        try
        {
            var responseJson = await ExecuteWithRetryAsync(url, "Geocoding Search", maxRetries: 1);
            var results = JsonSerializer.Deserialize<GeocodingResponse>(responseJson, _jsonOptions)?.Results ?? [];

            // Cache geocoding results for 24 hours
            _cache.Set(cacheKey, results, TimeSpan.FromHours(24));
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geocoding search failed for '{Name}'", name);
            return [];
        }
    }

    private async Task<string> ExecuteWithRetryAsync(string url, string operationName, int maxRetries = 2)
    {
        int attempt = 0;
        int delayMs = 1000;

        while (true)
        {
            attempt++;
            try
            {
                using var response = await _http.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                var statusCode = response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync();

                // Check for 429 Too Many Requests
                if (statusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("{Operation} HTTP 429 on attempt {Attempt}/{MaxAttempts}", operationName, attempt, maxRetries + 1);

                    TimeSpan? retryAfter = null;
                    if (response.Headers.RetryAfter != null)
                    {
                        if (response.Headers.RetryAfter.Delta.HasValue)
                            retryAfter = response.Headers.RetryAfter.Delta.Value;
                        else if (response.Headers.RetryAfter.Date.HasValue)
                            retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    }

                    // If max retries reached or Retry-After is too long (> 10s), stop and set cooldown
                    if (attempt > maxRetries || (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 10))
                    {
                        var cooldown = retryAfter ?? TimeSpan.FromSeconds(20);
                        if (cooldown > TimeSpan.FromMinutes(1)) cooldown = TimeSpan.FromMinutes(1);
                        var msg = "The live weather service rate limit has been reached (HTTP 429). Live weather data is temporarily unavailable. Please wait a moment before refreshing.";

                        _cache.Set("weather_ratelimit_cooldown", msg, cooldown);
                        throw new WeatherRateLimitException(msg, retryAfter);
                    }

                    // Exponential backoff or Retry-After delay
                    var waitDuration = retryAfter ?? TimeSpan.FromMilliseconds(delayMs);
                    if (waitDuration < TimeSpan.FromMilliseconds(500)) waitDuration = TimeSpan.FromMilliseconds(500);
                    if (waitDuration > TimeSpan.FromSeconds(8)) waitDuration = TimeSpan.FromSeconds(8);

                    await Task.Delay(waitDuration);
                    delayMs *= 2;
                    continue;
                }

                // Check for 5xx Server Errors (transient)
                if ((int)statusCode >= 500 && (int)statusCode <= 599)
                {
                    _logger.LogWarning("{Operation} HTTP {StatusCode} on attempt {Attempt}/{MaxAttempts}", operationName, (int)statusCode, attempt, maxRetries + 1);

                    if (attempt > maxRetries)
                    {
                        throw new WeatherServiceException($"The Open-Meteo weather service is temporarily unavailable (HTTP {(int)statusCode}). Please try again later.");
                    }

                    await Task.Delay(delayMs);
                    delayMs *= 2;
                    continue;
                }

                // 4xx Client Errors (400, 404, etc.) - do not retry
                string errorReason = ExtractReasonFromJson(errorBody) ?? $"HTTP {(int)statusCode} ({statusCode})";
                throw new WeatherServiceException($"Weather service request failed: {errorReason}");
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "{Operation} timed out on attempt {Attempt}/{MaxAttempts}", operationName, attempt, maxRetries + 1);
                if (attempt > maxRetries)
                {
                    throw new WeatherServiceException("The request to the weather service timed out. Please try again.");
                }
                await Task.Delay(delayMs);
                delayMs *= 2;
            }
            catch (HttpRequestException ex) when (attempt <= maxRetries)
            {
                _logger.LogWarning(ex, "{Operation} network error on attempt {Attempt}/{MaxAttempts}", operationName, attempt, maxRetries + 1);
                await Task.Delay(delayMs);
                delayMs *= 2;
            }
            catch (WeatherServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} unexpected error on attempt {Attempt}", operationName, attempt);
                throw new WeatherServiceException("An error occurred while connecting to the weather service: " + ex.Message, ex);
            }
        }
    }

    private static string? ExtractReasonFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("reason", out var reasonProp))
            {
                return reasonProp.GetString();
            }
        }
        catch { }
        return null;
    }

    public static string DescribeWeather(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Unknown"
    };

    public static string MakeRecommendation(CurrentWeather? c)
    {
        if (c is null)
            return "Weather data is currently unavailable.";

        if (c.Precipitation >= 2 || c.Humidity >= 85)
            return "Recent precipitation or high humidity is present. Consider postponing irrigation and inspect field soil moisture before watering.";

        if (c.Temperature >= 35)
            return "High temperature detected. Check crop water stress and monitor plants for heat stress.";

        if (c.Temperature >= 30)
            return "Warm conditions detected. Monitor soil moisture and plan irrigation according to crop requirements.";

        if (c.WindSpeed >= 30)
            return "Wind speed is elevated. Avoid spraying during strong winds.";

        return "Conditions appear moderate. Continue routine field monitoring and base irrigation on measured soil moisture.";
    }
}
