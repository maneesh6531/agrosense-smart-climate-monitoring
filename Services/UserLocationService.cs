using System.Globalization;
using ClimateMonitoringSystem.Models;
using Microsoft.AspNetCore.Http;

namespace ClimateMonitoringSystem.Services;

public interface IUserLocationService
{
    LocationSelection DefaultLocation { get; }
    LocationSelection GetCurrentLocation(HttpContext? context);
    LocationSelection SetLocation(HttpContext? context, double latitude, double longitude, string location);
    LocationSelection ResolveLocation(HttpContext? context, double? latitude, double? longitude, string? location);
}

public class UserLocationService : IUserLocationService
{
    public const string SessionKeyLocation = "AgroSense_Location";
    public const string SessionKeyLatitude = "AgroSense_Latitude";
    public const string SessionKeyLongitude = "AgroSense_Longitude";

    public const string CookieKeyLocation = "AgroSense_Location";
    public const string CookieKeyLatitude = "AgroSense_Latitude";
    public const string CookieKeyLongitude = "AgroSense_Longitude";

    public LocationSelection DefaultLocation => new("Jalandhar", 31.2240, 75.7690);

    public LocationSelection GetCurrentLocation(HttpContext? context)
    {
        if (context == null)
            return DefaultLocation;

        // 1. Try reading from Session
        try
        {
            var sessionLoc = context.Session.GetString(SessionKeyLocation);
            var sessionLatStr = context.Session.GetString(SessionKeyLatitude);
            var sessionLonStr = context.Session.GetString(SessionKeyLongitude);

            if (!string.IsNullOrWhiteSpace(sessionLoc) &&
                double.TryParse(sessionLatStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sessionLat) &&
                double.TryParse(sessionLonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sessionLon))
            {
                return new LocationSelection(sessionLoc, sessionLat, sessionLon);
            }
        }
        catch
        {
            // Session access may fail if session middleware is unavailable
        }

        // 2. Try reading from Cookies
        var cookieLoc = context.Request.Cookies[CookieKeyLocation];
        var cookieLatStr = context.Request.Cookies[CookieKeyLatitude];
        var cookieLonStr = context.Request.Cookies[CookieKeyLongitude];

        if (!string.IsNullOrWhiteSpace(cookieLoc) &&
            double.TryParse(cookieLatStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var cookieLat) &&
            double.TryParse(cookieLonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var cookieLon))
        {
            // Sync to session for fast retrieval in current session
            try
            {
                context.Session.SetString(SessionKeyLocation, cookieLoc);
                context.Session.SetString(SessionKeyLatitude, cookieLat.ToString(CultureInfo.InvariantCulture));
                context.Session.SetString(SessionKeyLongitude, cookieLon.ToString(CultureInfo.InvariantCulture));
            }
            catch { }

            return new LocationSelection(cookieLoc, cookieLat, cookieLon);
        }

        // 3. Fallback to initial default
        return DefaultLocation;
    }

    public LocationSelection SetLocation(HttpContext? context, double latitude, double longitude, string location)
    {
        // Validate coordinates
        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < -90 || latitude > 90 ||
            double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < -180 || longitude > 180)
        {
            return GetCurrentLocation(context);
        }

        var cleanLocation = string.IsNullOrWhiteSpace(location) ? $"{latitude:F4}, {longitude:F4}" : location.Trim();
        var normalized = new LocationSelection(cleanLocation, latitude, longitude);

        if (context != null)
        {
            var latStr = latitude.ToString(CultureInfo.InvariantCulture);
            var lonStr = longitude.ToString(CultureInfo.InvariantCulture);

            // Save to Session
            try
            {
                context.Session.SetString(SessionKeyLocation, cleanLocation);
                context.Session.SetString(SessionKeyLatitude, latStr);
                context.Session.SetString(SessionKeyLongitude, lonStr);
            }
            catch { }

            // Save to Persistent Cookie (30 days)
            try
            {
                var cookieOptions = new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    HttpOnly = false, // Allow client-side JS to sync if needed
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = false
                };

                context.Response.Cookies.Append(CookieKeyLocation, cleanLocation, cookieOptions);
                context.Response.Cookies.Append(CookieKeyLatitude, latStr, cookieOptions);
                context.Response.Cookies.Append(CookieKeyLongitude, lonStr, cookieOptions);
            }
            catch { }
        }

        return normalized;
    }

    public LocationSelection ResolveLocation(HttpContext? context, double? latitude, double? longitude, string? location)
    {
        if (latitude.HasValue && longitude.HasValue)
        {
            var locName = !string.IsNullOrWhiteSpace(location)
                ? location
                : GetCurrentLocation(context).Location;

            return SetLocation(context, latitude.Value, longitude.Value, locName);
        }

        return GetCurrentLocation(context);
    }
}
