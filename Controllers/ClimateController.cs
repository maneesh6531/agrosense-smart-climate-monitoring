using ClimateMonitoringSystem.Models;
using ClimateMonitoringSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClimateMonitoringSystem.Controllers;

public class ClimateController : Controller
{
    private readonly WeatherService _weather;
    public ClimateController(WeatherService weather) => _weather = weather;

    private async Task<ClimatePageViewModel> BuildModel(double lat, double lon, string location)
    {
        var data = await _weather.GetWeatherAsync(lat, lon);
        return new ClimatePageViewModel
        {
            Location = location,
            Latitude = lat,
            Longitude = lon,
            Weather = data,
            WeatherDescription = WeatherService.DescribeWeather(data.Current?.WeatherCode ?? -1),
            Recommendation = WeatherService.MakeRecommendation(data.Current)
        };
    }

    public async Task<IActionResult> Dashboard(double latitude = 31.2240, double longitude = 75.7690, string location = "Jalandhar")
    {
        try { return View(await BuildModel(latitude, longitude, location)); }
        catch (Exception ex)
        {
            return View(new ClimatePageViewModel { Location = location, Latitude = latitude, Longitude = longitude, Error = "Unable to load live weather. Check your internet connection and try again. " + ex.Message });
        }
    }

    public async Task<IActionResult> Map(double latitude = 31.2240, double longitude = 75.7690, string location = "Jalandhar")
    {
        try { return View(await BuildModel(latitude, longitude, location)); }
        catch (Exception ex) { return View(new ClimatePageViewModel { Location = location, Latitude = latitude, Longitude = longitude, Error = "Weather data unavailable: " + ex.Message }); }
    }

    public async Task<IActionResult> Analytics(double latitude = 31.2240, double longitude = 75.7690, string location = "Jalandhar")
    {
        try { return View(await BuildModel(latitude, longitude, location)); }
        catch (Exception ex) { return View(new ClimatePageViewModel { Location = location, Latitude = latitude, Longitude = longitude, Error = "Analytics data unavailable: " + ex.Message }); }
    }

    public async Task<IActionResult> Forecast(double latitude = 31.2240, double longitude = 75.7690, string location = "Jalandhar")
    {
        try { return View(await BuildModel(latitude, longitude, location)); }
        catch (Exception ex) { return View(new ClimatePageViewModel { Location = location, Latitude = latitude, Longitude = longitude, Error = "Forecast unavailable: " + ex.Message }); }
    }

    public async Task<IActionResult> Recommendations(double latitude = 31.2240, double longitude = 75.7690, string location = "Jalandhar")
    {
        try { return View(await BuildModel(latitude, longitude, location)); }
        catch (Exception ex) { return View(new ClimatePageViewModel { Location = location, Latitude = latitude, Longitude = longitude, Error = "Recommendations unavailable: " + ex.Message }); }
    }

    [HttpGet]
    public async Task<IActionResult> SearchPlaces(string q)
    {
        try
        {
            var places = await _weather.SearchPlacesAsync(q);
            return Json(places.Select(p => new { name = p.Name, region = p.Region, country = p.Country, latitude = p.Latitude, longitude = p.Longitude }));
        }
        catch { return Json(Array.Empty<object>()); }
    }
}
