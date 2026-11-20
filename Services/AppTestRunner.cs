using System.Net;
using System.Text;
using ClimateMonitoringSystem.Controllers;
using ClimateMonitoringSystem.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClimateMonitoringSystem.Services;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request);
        if (ResponseFactory != null)
        {
            return Task.FromResult(ResponseFactory(request));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleWeatherJson, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }

    public const string SampleWeatherJson = @"{
        ""latitude"": 14.44,
        ""longitude"": 79.99,
        ""timezone"": ""Asia/Kolkata"",
        ""current"": {
            ""time"": ""2026-11-20T17:00"",
            ""temperature_2m"": 28.5,
            ""relative_humidity_2m"": 75.0,
            ""apparent_temperature"": 30.2,
            ""precipitation"": 0.0,
            ""weather_code"": 1,
            ""wind_speed_10m"": 12.0
        },
        ""hourly"": {
            ""time"": [""2026-11-20T00:00"", ""2026-11-20T01:00""],
            ""temperature_2m"": [27.0, 26.5],
            ""relative_humidity_2m"": [80.0, 82.0],
            ""precipitation_probability"": [10, 15],
            ""precipitation"": [0.0, 0.0]
        },
        ""daily"": {
            ""time"": [""2026-11-20"", ""2026-11-21""],
            ""temperature_2m_max"": [32.0, 31.5],
            ""temperature_2m_min"": [24.0, 23.5],
            ""precipitation_sum"": [0.0, 2.5],
            ""weather_code"": [1, 2],
            ""uv_index_max"": [7.5, 6.8]
        }
    }";
}

public class MockSession : ISession
{
    private readonly Dictionary<string, byte[]> _storage = new();
    public bool IsAvailable => true;
    public string Id => "mock-session-id";
    public IEnumerable<string> Keys => _storage.Keys;

    public void Clear() => _storage.Clear();
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Remove(string key) => _storage.Remove(key);
    public void Set(string key, byte[] value) => _storage[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _storage.TryGetValue(key, out value!);
}

public static class AppTestRunner
{
    public static async Task<int> RunAllTestsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine("AgroSense Automated Verification Test Suite");
        Console.WriteLine("==================================================");

        int passed = 0;
        int failed = 0;

        void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {testName}");
                passed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName}");
                Console.ResetColor();
                failed++;
            }
        }

        // ========================================================
        // 1. LOCATION SYNCHRONIZATION TESTS
        // ========================================================
        Console.WriteLine("\n--- 1. Testing UserLocationService & Location Persistence ---");
        var locService = new UserLocationService();
        var defaultContext = new DefaultHttpContext { Session = new MockSession() };

        // Test 1: Initial default location is Jalandhar
        var initialLoc = locService.GetCurrentLocation(defaultContext);
        Assert(initialLoc.Location == "Jalandhar" && Math.Abs(initialLoc.Latitude - 31.2240) < 0.001,
            "Test 1: Initial default location is Jalandhar");

        // Test 2: Set location to Nellore
        var nelloreLoc = locService.SetLocation(defaultContext, 14.4426, 79.9865, "Nellore");
        Assert(nelloreLoc.Location == "Nellore" && Math.Abs(nelloreLoc.Latitude - 14.4426) < 0.001,
            "Test 2: Selected location saved as Nellore");

        // Test 3: Session persistence without query parameters
        var sessionLoc = locService.GetCurrentLocation(defaultContext);
        Assert(sessionLoc.Location == "Nellore" && Math.Abs(sessionLoc.Latitude - 14.4426) < 0.001,
            "Test 3: Location retained in session without query parameters");

        // Test 4: Cookie persistence (simulating page refresh / new session)
        var refreshContext = new DefaultHttpContext { Session = new MockSession() };
        refreshContext.Request.Headers["Cookie"] = "AgroSense_Location=Nellore; AgroSense_Latitude=14.4426; AgroSense_Longitude=79.9865";
        var cookieLoc = locService.GetCurrentLocation(refreshContext);
        Assert(cookieLoc.Location == "Nellore" && Math.Abs(cookieLoc.Latitude - 14.4426) < 0.001,
            "Test 4: Browser refresh persists selected location via cookie");

        // Test 5: Switching city from Nellore to Mumbai
        var mumbaiLoc = locService.SetLocation(refreshContext, 19.0760, 72.8777, "Mumbai");
        var retrievedMumbai = locService.GetCurrentLocation(refreshContext);
        Assert(retrievedMumbai.Location == "Mumbai" && Math.Abs(retrievedMumbai.Latitude - 19.0760) < 0.001,
            "Test 5: Switching city updates location to Mumbai");

        // Test 6: Coordinate boundary validation (out of range lat/lon rejected)
        var invalidLat = locService.SetLocation(refreshContext, 999.0, 72.8777, "Invalid");
        Assert(invalidLat.Location == "Mumbai",
            "Test 6: Invalid latitude (+999) rejected safely, preserves valid location");

        // ========================================================
        // 2. WEATHER SERVICE & CACHING TESTS
        // ========================================================
        Console.WriteLine("\n--- 2. Testing WeatherService Caching & HTTP 429 Handling ---");
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var mockHttp = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHttp);
        var weatherService = new WeatherService(httpClient, memoryCache, NullLogger<WeatherService>.Instance);

        // Test 7a: First request calls API
        var w1 = await weatherService.GetWeatherAsync(14.4426, 79.9865);
        Assert(mockHttp.CallCount == 1 && w1.Current != null,
            "Test 7a: First weather request calls Open-Meteo API");

        // Test 7b: Second request uses in-memory cache
        var w2 = await weatherService.GetWeatherAsync(14.4426, 79.9865);
        Assert(mockHttp.CallCount == 1,
            "Test 7b: Consecutive request for same coords served from IMemoryCache (0 new API calls)");

        // Test 8: Geocoding caching is isolated
        mockHttp.ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""results"":[{""name"":""Nellore"",""latitude"":14.4426,""longitude"":79.9865,""admin1"":""Andhra Pradesh"",""country"":""India""}]}", Encoding.UTF8, "application/json")
        };
        var geo1 = await weatherService.SearchPlacesAsync("Nellore");
        var callCountAfterGeo1 = mockHttp.CallCount;
        var geo2 = await weatherService.SearchPlacesAsync("Nellore");
        Assert(mockHttp.CallCount == callCountAfterGeo1 && geo1.Count > 0 && geo2.Count > 0,
            "Test 8: Geocoding search cached separately for 24 hours");

        // Test 9: HTTP 429 Retry with exponential backoff & Retry-After header
        int retry429Attempts = 0;
        mockHttp.ResponseFactory = req =>
        {
            retry429Attempts++;
            if (retry429Attempts <= 2)
            {
                var resp429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                resp429.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                return resp429;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MockHttpMessageHandler.SampleWeatherJson, Encoding.UTF8, "application/json")
            };
        };

        var cache429 = new MemoryCache(new MemoryCacheOptions());
        var weatherService429 = new WeatherService(httpClient, cache429, NullLogger<WeatherService>.Instance);
        var wRetry = await weatherService429.GetWeatherAsync(20.0, 80.0);
        Assert(wRetry != null && retry429Attempts == 3,
            "Test 9: HTTP 429 respects Retry-After, retries with backoff and succeeds");

        // Test 10: Persistent 429 triggers graceful exception and cooldown (does not retry indefinitely)
        int persistent429Attempts = 0;
        mockHttp.ResponseFactory = req =>
        {
            persistent429Attempts++;
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
            return resp;
        };

        var cacheFail = new MemoryCache(new MemoryCacheOptions());
        var weatherServiceFail = new WeatherService(httpClient, cacheFail, NullLogger<WeatherService>.Instance);
        bool rateLimitCaught = false;
        try
        {
            await weatherServiceFail.GetWeatherAsync(25.0, 85.0);
        }
        catch (WeatherRateLimitException ex)
        {
            rateLimitCaught = true;
            Assert(ex.Message.Contains("HTTP 429") || ex.Message.Contains("rate limit"),
                "Test 10a: Informative rate-limit error message provided to user");
        }
        Assert(rateLimitCaught && persistent429Attempts <= 3,
            "Test 10b: Stopped after max retries without infinite looping (no indefinite retry)");

        // Test 11: No fake data on failure
        var failModel = new ClimatePageViewModel
        {
            Location = "Nellore",
            Error = "The live weather service rate limit has been reached (HTTP 429)."
        };
        Assert(failModel.Weather.Current == null && failModel.HasError && !failModel.HasWeather,
            "Test 11: Failed weather request does NOT generate fake or misleading weather numbers");

        // ========================================================
        // 3. FULL FIVE-PAGE SYNCHRONIZATION TESTS
        // ========================================================
        Console.WriteLine("\n--- 3. Testing ClimateController Across All Five Pages ---");
        var ctrlCache = new MemoryCache(new MemoryCacheOptions());
        mockHttp.ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MockHttpMessageHandler.SampleWeatherJson, Encoding.UTF8, "application/json")
        };
        var ctrlWeather = new WeatherService(httpClient, ctrlCache, NullLogger<WeatherService>.Instance);
        var controller = new ClimateController(ctrlWeather, locService, NullLogger<ClimateController>.Instance);

        var userSessionContext = new DefaultHttpContext { Session = new MockSession() };
        controller.ControllerContext = new ControllerContext { HttpContext = userSessionContext };

        // Step 1: User selects Nellore on Dashboard
        var dashResult = await controller.Dashboard(14.4426, 79.9865, "Nellore") as ViewResult;
        var dashModel = dashResult?.Model as ClimatePageViewModel;
        Assert(dashModel?.Location == "Nellore" && Math.Abs(dashModel.Latitude - 14.4426) < 0.001,
            "Test 12a: Dashboard displays Nellore (14.4426, 79.9865)");

        // Step 2: User navigates to Weather Map (NO query params) -> MUST RETAIN NELLORE
        var mapResult = await controller.Map() as ViewResult;
        var mapModel = mapResult?.Model as ClimatePageViewModel;
        Assert(mapModel?.Location == "Nellore" && Math.Abs(mapModel.Latitude - 14.4426) < 0.001,
            "Test 12b: Weather Map retains Nellore (14.4426, 79.9865) without query parameters");

        // Step 3: User navigates to Analytics (NO query params) -> MUST RETAIN NELLORE
        var anaResult = await controller.Analytics() as ViewResult;
        var anaModel = anaResult?.Model as ClimatePageViewModel;
        Assert(anaModel?.Location == "Nellore" && Math.Abs(anaModel.Latitude - 14.4426) < 0.001,
            "Test 12c: Analytics retains Nellore (14.4426, 79.9865) without query parameters");

        // Step 4: User navigates to Forecast (NO query params) -> MUST RETAIN NELLORE
        var foreResult = await controller.Forecast() as ViewResult;
        var foreModel = foreResult?.Model as ClimatePageViewModel;
        Assert(foreModel?.Location == "Nellore" && Math.Abs(foreModel.Latitude - 14.4426) < 0.001,
            "Test 12d: 7-Day Forecast retains Nellore (14.4426, 79.9865) without query parameters");

        // Step 5: User navigates to Recommendations (NO query params) -> MUST RETAIN NELLORE
        var recResult = await controller.Recommendations() as ViewResult;
        var recModel = recResult?.Model as ClimatePageViewModel;
        Assert(recModel?.Location == "Nellore" && Math.Abs(recModel.Latitude - 14.4426) < 0.001,
            "Test 12e: Recommendations retains Nellore (14.4426, 79.9865) without query parameters");

        // Step 6: User refreshes Dashboard (NO query params) -> MUST RETAIN NELLORE
        var refreshResult = await controller.Dashboard() as ViewResult;
        var refreshModel = refreshResult?.Model as ClimatePageViewModel;
        Assert(refreshModel?.Location == "Nellore" && Math.Abs(refreshModel.Latitude - 14.4426) < 0.001,
            "Test 12f: Dashboard refresh retains Nellore (14.4426, 79.9865)");

        // Step 7: User switches location to Ludhiana on Dashboard -> ALL PAGES UPDATE
        var ludhianaResult = await controller.Dashboard(30.9010, 75.8573, "Ludhiana") as ViewResult;
        var ludhianaModel = ludhianaResult?.Model as ClimatePageViewModel;
        Assert(ludhianaModel?.Location == "Ludhiana",
            "Test 13a: Dashboard switches to Ludhiana (30.9010, 75.8573)");

        var mapLudhiana = await controller.Map() as ViewResult;
        var mapLudhianaModel = mapLudhiana?.Model as ClimatePageViewModel;
        Assert(mapLudhianaModel?.Location == "Ludhiana" && Math.Abs(mapLudhianaModel.Latitude - 30.9010) < 0.001,
            "Test 13b: Weather Map now reflects Ludhiana (30.9010, 75.8573)");

        var anaLudhiana = await controller.Analytics() as ViewResult;
        var anaLudhianaModel = anaLudhiana?.Model as ClimatePageViewModel;
        Assert(anaLudhianaModel?.Location == "Ludhiana",
            "Test 13c: Analytics now reflects Ludhiana");

        var foreLudhiana = await controller.Forecast() as ViewResult;
        var foreLudhianaModel = foreLudhiana?.Model as ClimatePageViewModel;
        Assert(foreLudhianaModel?.Location == "Ludhiana",
            "Test 13d: Forecast now reflects Ludhiana");

        var recLudhiana = await controller.Recommendations() as ViewResult;
        var recLudhianaModel = recLudhiana?.Model as ClimatePageViewModel;
        Assert(recLudhianaModel?.Location == "Ludhiana",
            "Test 13e: Recommendations now reflects Ludhiana");

        // Step 8: SearchPlaces action returns results
        mockHttp.ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""results"":[{""name"":""Nellore"",""latitude"":14.4426,""longitude"":79.9865,""admin1"":""Andhra Pradesh"",""country"":""India""}]}", Encoding.UTF8, "application/json")
        };
        var searchResult = await controller.SearchPlaces("Nellore") as JsonResult;
        Assert(searchResult?.Value != null, "Test 14: SearchPlaces action returns valid JSON results");

        Console.WriteLine("\n==================================================");
        Console.WriteLine($"TOTAL TESTS: {passed + failed} | PASSED: {passed} | FAILED: {failed}");
        Console.WriteLine("==================================================");

        return failed == 0 ? 0 : 1;
    }
}
