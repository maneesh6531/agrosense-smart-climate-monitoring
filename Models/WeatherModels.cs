using System.Text.Json.Serialization;

namespace ClimateMonitoringSystem.Models;

public class WeatherResponse
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "";
    [JsonPropertyName("current")] public CurrentWeather? Current { get; set; }
    [JsonPropertyName("hourly")] public HourlyWeather? Hourly { get; set; }
    [JsonPropertyName("daily")] public DailyWeather? Daily { get; set; }
    [JsonPropertyName("fetched_at")] public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool IsStale { get; set; } = false;
    [JsonIgnore] public DateTimeOffset? LastUpdated { get; set; }
    [JsonIgnore] public string? ConditionText { get; set; }
}

public class CurrentWeather
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double Humidity { get; set; }
    [JsonPropertyName("apparent_temperature")] public double FeelsLike { get; set; }
    [JsonPropertyName("precipitation")] public double Precipitation { get; set; }
    [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double WindSpeed { get; set; }
    [JsonPropertyName("condition_text")] public string ConditionText { get; set; } = "";
}

public class HourlyWeather
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public List<double> Temperature { get; set; } = [];
    [JsonPropertyName("relative_humidity_2m")] public List<double> Humidity { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<double> RainProbability { get; set; } = [];
    [JsonPropertyName("precipitation")] public List<double> Precipitation { get; set; } = [];
}

public class DailyWeather
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")] public List<double> MaxTemperature { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")] public List<double> MinTemperature { get; set; } = [];
    [JsonPropertyName("precipitation_sum")] public List<double> Rainfall { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("uv_index_max")] public List<double> UvIndex { get; set; } = [];
    [JsonPropertyName("condition_text")] public List<string> ConditionText { get; set; } = [];
}

public class PlaceResult
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("lat")] public double Lat { set => Latitude = value; }
    [JsonPropertyName("lon")] public double Lon { set => Longitude = value; }
    [JsonPropertyName("region")] public string RegionAlt { set => Region = value; }
    [JsonPropertyName("admin1")] public string Region { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
}

public class GeocodingResponse
{
    [JsonPropertyName("results")] public List<PlaceResult> Results { get; set; } = [];
}

public class LocationSelection
{
    public string Location { get; set; } = "Jalandhar";
    public double Latitude { get; set; } = 31.2240;
    public double Longitude { get; set; } = 75.7690;

    public LocationSelection() { }

    public LocationSelection(string location, double latitude, double longitude)
    {
        Location = location;
        Latitude = latitude;
        Longitude = longitude;
    }
}

public class ClimatePageViewModel
{
    public string Location { get; set; } = "Jalandhar";
    public double Latitude { get; set; } = 31.2240;
    public double Longitude { get; set; } = 75.7690;
    public WeatherResponse Weather { get; set; } = new();
    public string WeatherDescription { get; set; } = "Unavailable";
    public string Recommendation { get; set; } = "";
    public string Error { get; set; } = "";
    public bool IsStale { get; set; } = false;
    public DateTimeOffset? LastUpdated { get; set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasWeather => Weather?.Current != null;
}
