using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ClimateMonitoringSystem.Controllers;
using ClimateMonitoringSystem.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClimateMonitoringSystem.Services;

public class MockWeatherApiHttpMessageHandler : HttpMessageHandler
{
    public int TotalRequests => _requests.Count;
    public int MaxConcurrentRequests { get; private set; }
    private int _currentInFlight;
    private readonly ConcurrentBag<HttpRequestMessage> _requests = new();
    public List<HttpRequestMessage> RecordedRequests => _requests.ToList();
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? AsyncResponseFactory { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        var inFlight = Interlocked.Increment(ref _currentInFlight);
        lock (this)
        {
            if (inFlight > MaxConcurrentRequests)
            {
                MaxConcurrentRequests = inFlight;
            }
        }

        try
        {
            if (AsyncResponseFactory != null)
            {
                return await AsyncResponseFactory(request);
            }

            // Small delay to simulate network latency
            await Task.Delay(20, cancellationToken);

            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("search.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleSearchJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleForecastJson, Encoding.UTF8, "application/json")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _currentInFlight);
        }
    }

    public const string SampleForecastJson = @"{
        ""location"": {
            ""name"": ""Nellore"",
            ""region"": ""Andhra Pradesh"",
            ""country"": ""India"",
            ""lat"": 14.44,
            ""lon"": 79.99,
            ""tz_id"": ""Asia/Kolkata"",
            ""localtime"": ""2026-11-20 23:45""
        },
        ""current"": {
            ""last_updated"": ""2026-11-20 23:30"",
            ""temp_c"": 27.8,
            ""feelslike_c"": 30.5,
            ""humidity"": 76,
            ""precip_mm"": 0.0,
            ""wind_kph"": 11.5,
            ""condition"": {
                ""text"": ""Partly cloudy"",
                ""code"": 1003
            }
        },
        ""forecast"": {
            ""forecastday"": [
                {
                    ""date"": ""2026-11-20"",
                    ""day"": {
                        ""maxtemp_c"": 32.8,
                        ""mintemp_c"": 23.8,
                        ""totalprecip_mm"": 0.0,
                        ""uv"": 7.8,
                        ""condition"": { ""text"": ""Partly cloudy"", ""code"": 1003 }
                    },
                    ""hour"": [
                        { ""time"": ""2026-11-20 00:00"", ""temp_c"": 25.1, ""humidity"": 85, ""chance_of_rain"": 5, ""precip_mm"": 0.0 },
                        { ""time"": ""2026-11-20 01:00"", ""temp_c"": 24.8, ""humidity"": 86, ""chance_of_rain"": 5, ""precip_mm"": 0.0 },
                        { ""time"": ""2026-11-20 02:00"", ""temp_c"": 24.5, ""humidity"": 88, ""chance_of_rain"": 5, ""precip_mm"": 0.0 },
                        { ""time"": ""2026-11-20 03:00"", ""temp_c"": 24.2, ""humidity"": 89, ""chance_of_rain"": 5, ""precip_mm"": 0.0 }
                    ]
                },
                {
                    ""date"": ""2026-11-21"",
                    ""day"": {
                        ""maxtemp_c"": 32.2,
                        ""mintemp_c"": 23.5,
                        ""totalprecip_mm"": 0.5,
                        ""uv"": 7.5,
                        ""condition"": { ""text"": ""Sunny"", ""code"": 1000 }
                    },
                    ""hour"": [
                        { ""time"": ""2026-11-21 00:00"", ""temp_c"": 24.9, ""humidity"": 84, ""chance_of_rain"": 10, ""precip_mm"": 0.0 }
                    ]
                },
                {
                    ""date"": ""2026-11-22"",
                    ""day"": {
                        ""maxtemp_c"": 31.8,
                        ""mintemp_c"": 23.2,
                        ""totalprecip_mm"": 1.2,
                        ""uv"": 6.9,
                        ""condition"": { ""text"": ""Patchy rain possible"", ""code"": 1063 }
                    },
                    ""hour"": [
                        { ""time"": ""2026-11-22 00:00"", ""temp_c"": 24.5, ""humidity"": 85, ""chance_of_rain"": 20, ""precip_mm"": 0.1 }
                    ]
                }
            ]
        }
    }";

    public const string SampleSearchJson = @"[
        {
            ""id"": 1261029,
            ""name"": ""Nellore"",
            ""region"": ""Andhra Pradesh"",
            ""country"": ""India"",
            ""lat"": 14.44,
            ""lon"": 79.99,
            ""url"": ""nellore-andhra-pradesh-india""
        }
    ]";

    public const string SampleAuthErrorJson = @"{
        ""error"": {
            ""code"": 2006,
            ""message"": ""API key is invalid.""
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
        Console.WriteLine("======================================================================");
        Console.WriteLine("AgroSense WeatherAPI.com Integration & Verification Test Suite");
        Console.WriteLine("======================================================================");

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

        WeatherService.ResetCooldownForTesting();

        var testApiKey = "test_valid_weatherapi_key_12345";
        var configWithKey = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WEATHERAPI_KEY"] = testApiKey
            })
            .Build();

        var locService = new UserLocationService();

        // ==============================================================================
        // 1. LOCATION SYNCHRONIZATION TESTS
        // ==============================================================================
        Console.WriteLine("\n--- 1. Testing UserLocationService & Location Persistence ---");
        var defaultContext = new DefaultHttpContext { Session = new MockSession() };
        var initialLoc = locService.GetCurrentLocation(defaultContext);
        Assert(initialLoc.Location == "Jalandhar" && Math.Abs(initialLoc.Latitude - 31.2240) < 0.001,
            "Test 1a: Initial default location is Jalandhar");

        var nelloreLoc = locService.SetLocation(defaultContext, 14.4426, 79.9865, "Nellore");
        Assert(nelloreLoc.Location == "Nellore" && Math.Abs(nelloreLoc.Latitude - 14.4426) < 0.001,
            "Test 1b: Selected location saved as Nellore (14.4426, 79.9865)");

        var sessionLoc = locService.GetCurrentLocation(defaultContext);
        Assert(sessionLoc.Location == "Nellore" && Math.Abs(sessionLoc.Latitude - 14.4426) < 0.001,
            "Test 1c: Location retained in session without query parameters");

        var refreshContext = new DefaultHttpContext { Session = new MockSession() };
        refreshContext.Request.Headers["Cookie"] = "AgroSense_Location=Nellore; AgroSense_Latitude=14.4426; AgroSense_Longitude=79.9865";
        var cookieLoc = locService.GetCurrentLocation(refreshContext);
        Assert(cookieLoc.Location == "Nellore" && Math.Abs(cookieLoc.Latitude - 14.4426) < 0.001,
            "Test 1d: Browser refresh persists selected location via cookie");

        var mumbaiLoc = locService.SetLocation(refreshContext, 19.0760, 72.8777, "Mumbai");
        var retrievedMumbai = locService.GetCurrentLocation(refreshContext);
        Assert(retrievedMumbai.Location == "Mumbai" && Math.Abs(retrievedMumbai.Latitude - 19.0760) < 0.001,
            "Test 1e: Switching city updates location to Mumbai");

        var invalidLat = locService.SetLocation(refreshContext, 999.0, 72.8777, "Invalid");
        Assert(invalidLat.Location == "Mumbai",
            "Test 1f: Invalid latitude (+999) rejected safely, preserves valid location");

        // ==============================================================================
        // 2. WEATHERAPI AUTHENTICATION & CURRENT WEATHER RETRIEVAL
        // ==============================================================================
        Console.WriteLine("\n--- 2. Testing WeatherAPI Authentication & Current Weather ---");
        var memoryCache2 = new MemoryCache(new MemoryCacheOptions());
        var mockHttp2 = new MockWeatherApiHttpMessageHandler();
        var client2 = new HttpClient(mockHttp2);
        var weatherService2 = new WeatherService(client2, memoryCache2, configWithKey, NullLogger<WeatherService>.Instance);

        var weatherData = await weatherService2.GetWeatherAsync(14.4426, 79.9865);
        Assert(weatherData.Current != null, "Test 2a: WeatherAPI returned valid current weather object");
        Assert(Math.Abs(weatherData.Current!.Temperature - 27.8) < 0.01,
            $"Test 2b: Current temperature mapped correctly: {weatherData.Current.Temperature}°C");
        Assert(Math.Abs(weatherData.Current.Humidity - 76.0) < 0.01,
            $"Test 2c: Current humidity mapped correctly: {weatherData.Current.Humidity}%");
        Assert(weatherData.Current.ConditionText == "Partly cloudy",
            $"Test 2d: Condition text preserved: {weatherData.Current.ConditionText}");

        // ==============================================================================
        // 3. WEATHERAPI CITY AUTOCOMPLETE SEARCH (NELLORE)
        // ==============================================================================
        Console.WriteLine("\n--- 3. Testing WeatherAPI City Autocomplete Search ---");
        var searchResults = await weatherService2.SearchPlacesAsync("Nellore");
        Assert(searchResults.Count > 0, "Test 3a: City search returned results for Nellore");
        var firstResult = searchResults.FirstOrDefault();
        Assert(firstResult != null && firstResult.Name == "Nellore" && firstResult.Country == "India",
            $"Test 3b: City search parsed: {firstResult?.Name}, {firstResult?.Region}, {firstResult?.Country}");

        // ==============================================================================
        // 4. THREE-DAY FORECAST DATA INTEGRITY
        // ==============================================================================
        Console.WriteLine("\n--- 4. Testing Three-Day Forecast Data Integrity ---");
        Assert(weatherData.Daily != null && weatherData.Daily.Time.Count == 3,
            $"Test 4a: Three-day forecast provided by WeatherAPI (Daily count: {weatherData.Daily?.Time.Count})");
        Assert(weatherData.Daily!.MaxTemperature.Count == 3 && weatherData.Daily.MaxTemperature[0] == 32.8,
            "Test 4b: Day 1 Max Temperature correctly mapped (32.8°C)");
        Assert(weatherData.Daily.MinTemperature.Count == 3 && weatherData.Daily.MinTemperature[0] == 23.8,
            "Test 4c: Day 1 Min Temperature correctly mapped (23.8°C)");
        Assert(weatherData.Hourly != null && weatherData.Hourly.Time.Count > 0,
            $"Test 4d: Hourly forecast points extracted for trend chart (Hourly count: {weatherData.Hourly?.Time.Count})");

        // ==============================================================================
        // 5. CACHING & REQUEST COALESCING (SINGLE-FLIGHT)
        // ==============================================================================
        Console.WriteLine("\n--- 5. Testing Caching & Request Coalescing ---");
        var memoryCache5 = new MemoryCache(new MemoryCacheOptions());
        var mockHttp5 = new MockWeatherApiHttpMessageHandler();
        var client5 = new HttpClient(mockHttp5);
        var weatherService5 = new WeatherService(client5, memoryCache5, configWithKey, NullLogger<WeatherService>.Instance);

        // 5 simultaneous requests for the same coordinates
        var coalescingTasks = new List<Task<WeatherResponse>>();
        for (int i = 0; i < 5; i++)
        {
            coalescingTasks.Add(weatherService5.GetWeatherAsync(14.4426, 79.9865));
        }
        var coalescedResults = await Task.WhenAll(coalescingTasks);
        Assert(coalescedResults.Length == 5 && mockHttp5.TotalRequests == 1,
            $"Test 5a: Request coalescing verified: 5 concurrent callers triggered exactly 1 upstream HTTP request (Actual: {mockHttp5.TotalRequests})");

        // Subsequent request from cache
        var cachedResult = await weatherService5.GetWeatherAsync(14.4426, 79.9865);
        Assert(cachedResult != null && mockHttp5.TotalRequests == 1,
            "Test 5b: In-memory cache hit: zero additional HTTP requests made");

        // ==============================================================================
        // 6. MISSING OR INVALID API KEY ERROR HANDLING
        // ==============================================================================
        Console.WriteLine("\n--- 6. Testing Missing / Invalid API Key Error Handling ---");
        // Test missing key:
        var emptyConfig = new ConfigurationBuilder().Build();
        var memoryCacheEmpty = new MemoryCache(new MemoryCacheOptions());
        var weatherServiceNoKey = new WeatherService(new HttpClient(new MockWeatherApiHttpMessageHandler()), memoryCacheEmpty, emptyConfig, NullLogger<WeatherService>.Instance);

        bool missingKeyCaught = false;
        try
        {
            // Use unique coordinates so no seed cache exists
            await weatherServiceNoKey.GetWeatherAsync(88.1234, 88.5678);
        }
        catch (WeatherServiceException ex)
        {
            missingKeyCaught = true;
            Assert(ex.Message.Contains("WEATHERAPI_KEY") || ex.Message.Contains("key is missing"),
                "Test 6a: Missing API key produces clear descriptive error without crashing");
        }
        Assert(missingKeyCaught, "Test 6b: Missing API key was caught safely");

        // Test invalid key:
        var mockHttpInvalid = new MockWeatherApiHttpMessageHandler();
        mockHttpInvalid.AsyncResponseFactory = async req =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(MockWeatherApiHttpMessageHandler.SampleAuthErrorJson, Encoding.UTF8, "application/json")
            };
        };
        var invalidConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WEATHERAPI_KEY"] = "invalid_dummy_key"
            })
            .Build();
        var weatherServiceInvalid = new WeatherService(new HttpClient(mockHttpInvalid), new MemoryCache(new MemoryCacheOptions()), invalidConfig, NullLogger<WeatherService>.Instance);

        bool invalidKeyCaught = false;
        try
        {
            await weatherServiceInvalid.GetWeatherAsync(77.1234, 77.5678);
        }
        catch (WeatherServiceException ex)
        {
            invalidKeyCaught = true;
            Assert(ex.Message.Contains("invalid") || ex.Message.Contains("Authentication Error"),
                $"Test 6c: Invalid API key produces clear authentication error: '{ex.Message}'");
        }
        Assert(invalidKeyCaught, "Test 6d: Invalid API key handled without crashing");

        // ==============================================================================
        // 7. FULL FIVE-PAGE MVC CONTROLLER INTEGRATION & LOCATION SYNCHRONIZATION
        // ==============================================================================
        Console.WriteLine("\n--- 7. Testing Full Five-Page MVC Controller Synchronization ---");
        WeatherService.ResetCooldownForTesting();
        var ctrlCache = new MemoryCache(new MemoryCacheOptions());
        var ctrlHttp = new MockWeatherApiHttpMessageHandler();
        var ctrlClient = new HttpClient(ctrlHttp);
        var ctrlWeather = new WeatherService(ctrlClient, ctrlCache, configWithKey, NullLogger<WeatherService>.Instance);
        var controller = new ClimateController(ctrlWeather, locService, NullLogger<ClimateController>.Instance);

        var sharedContext = new DefaultHttpContext { Session = new MockSession() };
        controller.ControllerContext = new ControllerContext { HttpContext = sharedContext };

        // 1. Dashboard selection
        var rDash = await controller.Dashboard(14.4426, 79.9865, "Nellore") as ViewResult;
        var mDash = rDash?.Model as ClimatePageViewModel;
        Assert(mDash?.Location == "Nellore", "Test 7a: Dashboard displays selected location Nellore");

        // 2. Weather Map (no params)
        var rMap = await controller.Map() as ViewResult;
        var mMap = rMap?.Model as ClimatePageViewModel;
        Assert(mMap?.Location == "Nellore" && Math.Abs(mMap.Latitude - 14.4426) < 0.001,
            "Test 7b: Weather Map retains Nellore without query parameters");

        // 3. Analytics (no params)
        var rAna = await controller.Analytics() as ViewResult;
        var mAna = rAna?.Model as ClimatePageViewModel;
        Assert(mAna?.Location == "Nellore", "Test 7c: Analytics retains Nellore");
        Assert(mAna?.Weather?.Current != null && mAna.Weather.Daily != null,
            "Test 7d: Analytics successfully loaded metrics from WeatherAPI");

        // 4. Forecast (no params)
        var rFore = await controller.Forecast() as ViewResult;
        var mFore = rFore?.Model as ClimatePageViewModel;
        Assert(mFore?.Location == "Nellore", "Test 7e: Forecast retains Nellore");
        Assert(mFore?.Weather?.Daily?.Time.Count == 3,
            $"Test 7f: Forecast view model contains 3-day forecast (Count: {mFore?.Weather?.Daily?.Time.Count})");

        // 5. Recommendations (no params)
        var rRec = await controller.Recommendations() as ViewResult;
        var mRec = rRec?.Model as ClimatePageViewModel;
        Assert(mRec?.Location == "Nellore", "Test 7g: Recommendations retains Nellore");
        Assert(!string.IsNullOrEmpty(mRec?.Recommendation),
            $"Test 7h: Recommendations advisory generated: '{mRec?.Recommendation}'");

        // 6. Refresh simulation
        var rRefresh = await controller.Dashboard() as ViewResult;
        var mRefresh = rRefresh?.Model as ClimatePageViewModel;
        Assert(mRefresh?.Location == "Nellore", "Test 7i: Page refresh retains Nellore");

        // Verify navigation across all 5 pages made at most 1 upstream call
        Assert(ctrlHttp.TotalRequests <= 1,
            $"Test 7j: All 5 pages served from cache with zero duplicate upstream calls (Upstream calls: {ctrlHttp.TotalRequests})");

        // ==============================================================================
        // 8. SECURITY & API KEY SANITIZATION
        // ==============================================================================
        Console.WriteLine("\n--- 8. Confirming No API Key is Exposed ---");
        // Verify that .gitignore includes .env
        var gitignoreContent = File.Exists(".gitignore") ? File.ReadAllText(".gitignore") : "";
        Assert(gitignoreContent.Contains(".env"), "Test 8a: .gitignore contains .env entry");

        var dockerignoreContent = File.Exists(".dockerignore") ? File.ReadAllText(".dockerignore") : "";
        Assert(dockerignoreContent.Contains(".env"), "Test 8b: .dockerignore contains .env entry");

        Assert(File.Exists(".env.example"), "Test 8c: .env.example exists with placeholder");
        var envExampleContent = File.ReadAllText(".env.example");
        Assert(!envExampleContent.Contains(testApiKey) && envExampleContent.Contains("your_weatherapi_key_here"),
            "Test 8d: .env.example does not contain actual API key");

        Console.WriteLine("\n======================================================================");
        Console.WriteLine($"TOTAL TESTS: {passed + failed} | PASSED: {passed} | FAILED: {failed}");
        Console.WriteLine("======================================================================");

        return failed == 0 ? 0 : 1;
    }
}
