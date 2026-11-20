using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ClimateMonitoringSystem.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _config;
    private readonly ILogger<WeatherService> _logger;
    private readonly string _cacheDirectory;

    // 1. Shared application-wide semaphore: serializes upstream requests
    private static readonly SemaphoreSlim _apiSemaphore = new(1, 1);

    // 2. Request Coalescing (Single-Flight): concurrent requests for identical coordinates share one in-flight Task
    private static readonly ConcurrentDictionary<string, Task<WeatherResponse>> _inFlightForecasts = new();
    private static readonly ConcurrentDictionary<string, Task<List<PlaceResult>>> _inFlightGeocoding = new();

    // 3. Stale cache memory storage (persists beyond normal 15-minute cache expiration)
    private static readonly ConcurrentDictionary<string, WeatherResponse> _staleCacheMemory = new();

    // 4. Global cooldown after HTTP 429
    private static DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private static string _cooldownMessage = "";
    private static readonly object _cooldownLock = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WeatherService(HttpClient http, IMemoryCache cache, IConfiguration config, ILogger<WeatherService> logger)
    {
        _http = http;
        _cache = cache;
        _config = config;
        _logger = logger;

        // Ensure disk cache directory exists for persistent stale-cache fallback
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data", "cache");
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize disk cache directory {CacheDir}", _cacheDirectory);
        }
    }

    /// <summary>
    /// Retrieves the WeatherAPI key from environment variables or configuration.
    /// Never logs or hardcodes the key.
    /// </summary>
    public string? GetApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

        var cfgKey = _config["WEATHERAPI_KEY"] ?? _config["WEATHER_API_KEY"];
        if (!string.IsNullOrWhiteSpace(cfgKey)) return cfgKey.Trim();

        return null;
    }

    public static string GetWeatherCacheKey(double latitude, double longitude)
    {
        var lat = Math.Round(latitude, 4).ToString("F4", CultureInfo.InvariantCulture);
        var lon = Math.Round(longitude, 4).ToString("F4", CultureInfo.InvariantCulture);
        return $"weather_{lat}_{lon}";
    }

    private static string GetGeocodingCacheKey(string name)
    {
        return $"places_{name.Trim().ToLowerInvariant()}";
    }

    public static bool IsCooldownActive(out string message)
    {
        lock (_cooldownLock)
        {
            if (DateTimeOffset.UtcNow < _cooldownUntil)
            {
                var remaining = (_cooldownUntil - DateTimeOffset.UtcNow).TotalSeconds;
                message = string.IsNullOrEmpty(_cooldownMessage)
                    ? $"The weather service rate limit has been reached (HTTP 429). Cooldown active for {Math.Ceiling(remaining)}s."
                    : _cooldownMessage;
                return true;
            }

            message = "";
            return false;
        }
    }

    public static void SetCooldown(TimeSpan? duration, string reason)
    {
        lock (_cooldownLock)
        {
            var cooldown = duration ?? TimeSpan.FromSeconds(60);
            if (cooldown < TimeSpan.FromSeconds(30)) cooldown = TimeSpan.FromSeconds(30);
            if (cooldown > TimeSpan.FromMinutes(15)) cooldown = TimeSpan.FromMinutes(15);

            _cooldownUntil = DateTimeOffset.UtcNow.Add(cooldown);
            _cooldownMessage = reason;
        }
    }

    public static void ResetCooldownForTesting()
    {
        lock (_cooldownLock)
        {
            _cooldownUntil = DateTimeOffset.MinValue;
            _cooldownMessage = "";
        }
    }

    /// <summary>
    /// Retrieves weather forecast using WeatherAPI.com as the primary provider.
    /// Includes memory caching, request coalescing, global serialization, and stale-cache fallback.
    /// </summary>
    public async Task<WeatherResponse> GetWeatherAsync(double latitude, double longitude)
    {
        var cacheKey = GetWeatherCacheKey(latitude, longitude);

        // Tier 1: Check in-memory fresh cache (15 minutes retention)
        if (_cache.TryGetValue(cacheKey, out WeatherResponse? cachedResponse) && cachedResponse != null)
        {
            _logger.LogInformation("Weather cache HIT (Memory) for key: {CacheKey}", cacheKey);
            return cachedResponse;
        }

        // Tier 2: Request Coalescing (Single-Flight)
        return await _inFlightForecasts.GetOrAdd(cacheKey, _ => FetchForecastWithConcurrencyLimiterAsync(latitude, longitude, cacheKey));
    }

    private async Task<WeatherResponse> FetchForecastWithConcurrencyLimiterAsync(double latitude, double longitude, string cacheKey)
    {
        try
        {
            // Double-check cache in case another task populated it
            if (_cache.TryGetValue(cacheKey, out WeatherResponse? cached) && cached != null)
            {
                return cached;
            }

            // Check if rate-limit cooldown is active
            if (IsCooldownActive(out var cooldownMsg))
            {
                _logger.LogWarning("WeatherAPI cooldown active. Checking for stale cache fallback for {CacheKey}...", cacheKey);
                var stale = TryGetStaleFallback(cacheKey);
                if (stale != null)
                {
                    _logger.LogInformation("Serving stale cached weather during cooldown for {CacheKey}.", cacheKey);
                    return stale;
                }
                throw new WeatherRateLimitException(cooldownMsg);
            }

            // Verify API key presence
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("WEATHERAPI_KEY is not configured. Attempting stale fallback for {CacheKey}...", cacheKey);
                var stale = TryGetStaleFallback(cacheKey);
                if (stale != null) return stale;

                throw new WeatherServiceException(
                    "WeatherAPI key is missing. Please set the WEATHERAPI_KEY environment variable in your .env file or Render dashboard.");
            }

            // Acquire concurrency limiter
            _logger.LogInformation("Waiting for weather API semaphore for {CacheKey}...", cacheKey);
            bool acquired = await _apiSemaphore.WaitAsync(TimeSpan.FromSeconds(20));
            if (!acquired)
            {
                _logger.LogWarning("Weather semaphore queue timeout for {CacheKey}. Attempting stale fallback...", cacheKey);
                var stale = TryGetStaleFallback(cacheKey);
                if (stale != null) return stale;
                throw new WeatherServiceException("Weather service queue is currently busy. Please try again shortly.");
            }

            try
            {
                // Double-check cache again after queueing
                if (_cache.TryGetValue(cacheKey, out WeatherResponse? freshAfterWait) && freshAfterWait != null)
                {
                    return freshAfterWait;
                }

                _logger.LogInformation("Dispatching upstream WeatherAPI forecast request for {CacheKey}...", cacheKey);

                var lat = latitude.ToString(CultureInfo.InvariantCulture);
                var lon = longitude.ToString(CultureInfo.InvariantCulture);

                // WeatherAPI forecast.json provides current weather + 3-day forecast in a single request
                var url = $"https://api.weatherapi.com/v1/forecast.json?key={apiKey}&q={lat},{lon}&days=3&aqi=no&alerts=no";

                using var response = await _http.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    HandleApiErrorResponse(response.StatusCode, responseContent, response.Headers.RetryAfter);
                }

                var weatherData = ParseWeatherApiResponse(responseContent, latitude, longitude);

                // Cache successful response in memory and persistent disk cache
                CacheSuccessfulResponse(cacheKey, weatherData);

                return weatherData;
            }
            finally
            {
                await Task.Delay(200);
                _apiSemaphore.Release();
            }
        }
        catch (WeatherRateLimitException)
        {
            var stale = TryGetStaleFallback(cacheKey);
            if (stale != null)
            {
                _logger.LogWarning("Rate limit reached. Returning stale cache fallback for {CacheKey}.", cacheKey);
                return stale;
            }
            throw;
        }
        catch (Exception ex)
        {
            var stale = TryGetStaleFallback(cacheKey);
            if (stale != null)
            {
                _logger.LogWarning(ex, "Weather request failed. Returning stale cache fallback for {CacheKey}.", cacheKey);
                return stale;
            }
            throw;
        }
        finally
        {
            _inFlightForecasts.TryRemove(cacheKey, out var _);
        }
    }

    private void HandleApiErrorResponse(HttpStatusCode statusCode, string responseContent, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfterHeader)
    {
        string? errorReason = null;
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
            {
                errorReason = msg.GetString();
            }
        }
        catch { }

        if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
        {
            var message = string.IsNullOrWhiteSpace(errorReason)
                ? "Invalid or expired WeatherAPI key. Please verify WEATHERAPI_KEY in your .env or Render configuration."
                : $"WeatherAPI Authentication Error: {errorReason}";

            throw new WeatherServiceException(message);
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = null;
            if (retryAfterHeader != null)
            {
                if (retryAfterHeader.Delta.HasValue) retryAfter = retryAfterHeader.Delta.Value;
                else if (retryAfterHeader.Date.HasValue) retryAfter = retryAfterHeader.Date.Value - DateTimeOffset.UtcNow;
            }

            var cooldownTime = retryAfter ?? TimeSpan.FromSeconds(60);
            var msg = string.IsNullOrWhiteSpace(errorReason)
                ? "WeatherAPI rate limit reached (HTTP 429). The system is respecting cooldown."
                : $"WeatherAPI Rate Limit: {errorReason}";

            SetCooldown(cooldownTime, msg);
            throw new WeatherRateLimitException(msg, retryAfter);
        }

        throw new WeatherServiceException(
            $"WeatherAPI request failed (HTTP {(int)statusCode}): {(errorReason ?? "Unexpected response")}");
    }

    private WeatherResponse ParseWeatherApiResponse(string json, double latitude, double longitude)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new WeatherResponse
        {
            Latitude = latitude,
            Longitude = longitude,
            Timezone = root.TryGetProperty("location", out var loc) && loc.TryGetProperty("tz_id", out var tz)
                ? tz.GetString() ?? "auto"
                : "auto",
            FetchedAt = DateTimeOffset.UtcNow,
            IsStale = false,
            LastUpdated = DateTimeOffset.UtcNow
        };

        if (root.TryGetProperty("current", out var cur))
        {
            var condCode = cur.TryGetProperty("condition", out var cond) && cond.TryGetProperty("code", out var code)
                ? code.GetInt32()
                : 1000;
            var condText = cond.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";

            result.ConditionText = condText;
            result.Current = new CurrentWeather
            {
                Time = cur.TryGetProperty("last_updated", out var lu) ? lu.GetString() ?? "" : "",
                Temperature = cur.TryGetProperty("temp_c", out var t) ? t.GetDouble() : 0.0,
                Humidity = cur.TryGetProperty("humidity", out var h) ? h.GetDouble() : 0.0,
                FeelsLike = cur.TryGetProperty("feelslike_c", out var fl) ? fl.GetDouble() : 0.0,
                Precipitation = cur.TryGetProperty("precip_mm", out var p) ? p.GetDouble() : 0.0,
                WindSpeed = cur.TryGetProperty("wind_kph", out var w) ? w.GetDouble() : 0.0,
                WeatherCode = MapWeatherApiCodeToWmo(condCode),
                ConditionText = condText
            };
        }

        result.Hourly = new HourlyWeather();
        result.Daily = new DailyWeather();

        if (root.TryGetProperty("forecast", out var forecast) && forecast.TryGetProperty("forecastday", out var days))
        {
            foreach (var dayItem in days.EnumerateArray())
            {
                result.Daily.Time.Add(dayItem.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "");

                if (dayItem.TryGetProperty("day", out var dayObj))
                {
                    result.Daily.MaxTemperature.Add(dayObj.TryGetProperty("maxtemp_c", out var maxT) ? maxT.GetDouble() : 0.0);
                    result.Daily.MinTemperature.Add(dayObj.TryGetProperty("mintemp_c", out var minT) ? minT.GetDouble() : 0.0);
                    result.Daily.Rainfall.Add(dayObj.TryGetProperty("totalprecip_mm", out var rain) ? rain.GetDouble() : 0.0);
                    result.Daily.UvIndex.Add(dayObj.TryGetProperty("uv", out var uv) ? uv.GetDouble() : 5.0);

                    var dayCondCode = dayObj.TryGetProperty("condition", out var dayCond) && dayCond.TryGetProperty("code", out var cCode)
                        ? cCode.GetInt32()
                        : 1000;
                    var dayCondText = dayCond.TryGetProperty("text", out var cText) ? cText.GetString() ?? "" : "";

                    result.Daily.WeatherCode.Add(MapWeatherApiCodeToWmo(dayCondCode));
                    result.Daily.ConditionText.Add(dayCondText);
                }

                // Extract next 24 hourly data points from the forecast days
                if (result.Hourly.Time.Count < 24 && dayItem.TryGetProperty("hour", out var hours))
                {
                    foreach (var h in hours.EnumerateArray())
                    {
                        if (result.Hourly.Time.Count >= 24) break;
                        result.Hourly.Time.Add(h.TryGetProperty("time", out var ht) ? ht.GetString() ?? "" : "");
                        result.Hourly.Temperature.Add(h.TryGetProperty("temp_c", out var hTemp) ? hTemp.GetDouble() : 0.0);
                        result.Hourly.Humidity.Add(h.TryGetProperty("humidity", out var hHum) ? hHum.GetDouble() : 0.0);
                        result.Hourly.RainProbability.Add(h.TryGetProperty("chance_of_rain", out var hRainProb) ? hRainProb.GetDouble() : 0.0);
                        result.Hourly.Precipitation.Add(h.TryGetProperty("precip_mm", out var hPrecip) ? hPrecip.GetDouble() : 0.0);
                    }
                }
            }
        }

        return result;
    }

    private void CacheSuccessfulResponse(string cacheKey, WeatherResponse data)
    {
        _cache.Set(cacheKey, data, TimeSpan.FromMinutes(15));
        _staleCacheMemory[cacheKey] = data;

        try
        {
            var filePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist weather cache to disk for {CacheKey}", cacheKey);
        }
    }

    public WeatherResponse? TryGetStaleFallback(string cacheKey)
    {
        if (_staleCacheMemory.TryGetValue(cacheKey, out var memStale) && memStale != null)
        {
            var clone = CloneResponse(memStale);
            clone.IsStale = true;
            clone.LastUpdated = memStale.FetchedAt;
            return clone;
        }

        try
        {
            var filePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var diskData = JsonSerializer.Deserialize<WeatherResponse>(json, _jsonOptions);
                if (diskData?.Current != null)
                {
                    diskData.IsStale = true;
                    var fileInfo = new FileInfo(filePath);
                    diskData.LastUpdated = fileInfo.LastWriteTimeUtc;
                    _staleCacheMemory[cacheKey] = diskData;
                    return diskData;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading disk cache for {CacheKey}", cacheKey);
        }

        return null;
    }

    private static WeatherResponse CloneResponse(WeatherResponse src)
    {
        return new WeatherResponse
        {
            Latitude = src.Latitude,
            Longitude = src.Longitude,
            Timezone = src.Timezone,
            Current = src.Current,
            Hourly = src.Hourly,
            Daily = src.Daily,
            FetchedAt = src.FetchedAt,
            IsStale = true,
            LastUpdated = src.LastUpdated ?? src.FetchedAt,
            ConditionText = src.ConditionText
        };
    }

    /// <summary>
    /// Searches places with city autocomplete using WeatherAPI.com search.json.
    /// </summary>
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

        // 2. Deduplicate identical simultaneous searches
        return await _inFlightGeocoding.GetOrAdd(cacheKey, async _ =>
        {
            try
            {
                if (_cache.TryGetValue(cacheKey, out List<PlaceResult>? fresh) && fresh != null)
                {
                    return fresh;
                }

                var apiKey = GetApiKey();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.LogWarning("WEATHERAPI_KEY not configured for search autocomplete.");
                    return [];
                }

                var url = $"https://api.weatherapi.com/v1/search.json?key={apiKey}&q={Uri.EscapeDataString(name.Trim())}";
                using var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var results = JsonSerializer.Deserialize<List<PlaceResult>>(responseJson, _jsonOptions) ?? [];

                if (results.Count > 0)
                {
                    _cache.Set(cacheKey, results, TimeSpan.FromHours(24));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SearchPlaces failed for '{Name}'", name);
                return [];
            }
            finally
            {
                _inFlightGeocoding.TryRemove(cacheKey, out var _);
            }
        });
    }

    public static int MapWeatherApiCodeToWmo(int weatherApiCode) => weatherApiCode switch
    {
        1000 => 0, // Sunny / Clear
        1003 => 2, // Partly cloudy
        1006 or 1009 => 3, // Overcast
        1030 or 1135 or 1147 => 45, // Fog / Mist
        1063 or 1150 or 1153 or 1180 or 1183 => 61, // Light rain
        1186 or 1189 => 63, // Moderate rain
        1192 or 1195 or 1240 or 1243 or 1246 => 65, // Heavy rain
        1066 or 1114 or 1210 or 1213 or 1216 or 1219 or 1222 or 1225 => 71, // Snow
        1069 or 1198 or 1201 or 1204 or 1207 => 66, // Freezing rain / Sleet
        1087 or 1273 or 1276 or 1279 or 1282 => 95, // Thunderstorm
        _ => 1
    };

    public static string DescribeWeather(int code, string? conditionText = null)
    {
        if (!string.IsNullOrWhiteSpace(conditionText))
            return conditionText;

        return code switch
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
    }

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
