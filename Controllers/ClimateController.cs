using ClimateMonitoringSystem.Models;
using ClimateMonitoringSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClimateMonitoringSystem.Controllers;

public class ClimateController : Controller
{
    private readonly WeatherService _weather;
    private readonly IUserLocationService _locationService;
    private readonly ILogger<ClimateController> _logger;

    public ClimateController(WeatherService weather, IUserLocationService locationService, ILogger<ClimateController> logger)
    {
        _weather = weather;
        _locationService = locationService;
        _logger = logger;
    }

    private async Task<ClimatePageViewModel> BuildModelAsync(LocationSelection loc)
    {
        try
        {
            var data = await _weather.GetWeatherAsync(loc.Latitude, loc.Longitude);
            return new ClimatePageViewModel
            {
                Location = loc.Location,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Weather = data,
                WeatherDescription = WeatherService.DescribeWeather(data.Current?.WeatherCode ?? -1),
                Recommendation = WeatherService.MakeRecommendation(data.Current)
            };
        }
        catch (WeatherRateLimitException ex)
        {
            _logger.LogWarning("Rate limit reached for {Location}: {Message}", loc.Location, ex.Message);
            return new ClimatePageViewModel
            {
                Location = loc.Location,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Error = ex.Message
            };
        }
        catch (WeatherServiceException ex)
        {
            _logger.LogWarning("Weather service error for {Location}: {Message}", loc.Location, ex.Message);
            return new ClimatePageViewModel
            {
                Location = loc.Location,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Error = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading weather for {Location}", loc.Location);
            return new ClimatePageViewModel
            {
                Location = loc.Location,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Error = "Unable to load live weather at this time. " + ex.Message
            };
        }
    }

    [HttpGet]
    public async Task<IActionResult> Dashboard(double? latitude = null, double? longitude = null, string? location = null)
    {
        var loc = _locationService.ResolveLocation(HttpContext, latitude, longitude, location);
        var model = await BuildModelAsync(loc);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Map(double? latitude = null, double? longitude = null, string? location = null)
    {
        var loc = _locationService.ResolveLocation(HttpContext, latitude, longitude, location);
        var model = await BuildModelAsync(loc);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Analytics(double? latitude = null, double? longitude = null, string? location = null)
    {
        var loc = _locationService.ResolveLocation(HttpContext, latitude, longitude, location);
        var model = await BuildModelAsync(loc);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Forecast(double? latitude = null, double? longitude = null, string? location = null)
    {
        var loc = _locationService.ResolveLocation(HttpContext, latitude, longitude, location);
        var model = await BuildModelAsync(loc);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Recommendations(double? latitude = null, double? longitude = null, string? location = null)
    {
        var loc = _locationService.ResolveLocation(HttpContext, latitude, longitude, location);
        var model = await BuildModelAsync(loc);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> SearchPlaces(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Json(Array.Empty<object>());

        try
        {
            var places = await _weather.SearchPlacesAsync(q);
            return Json(places.Select(p => new
            {
                name = p.Name,
                region = p.Region,
                country = p.Country,
                latitude = p.Latitude,
                longitude = p.Longitude
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchPlaces failed for query {Query}", q);
            return Json(Array.Empty<object>());
        }
    }
}
