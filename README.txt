AGROCLIMATE - AUTOMATED CLIMATE MONITORING SYSTEM FOR SMART AGRICULTURE

REQUIREMENTS
- Visual Studio 2022 with ASP.NET and web development workload
- .NET 10 SDK
- Internet connection (Open-Meteo API and CDN assets)

RUN
1. Extract the ZIP.
2. Open ClimateMonitoringSystem.csproj in Visual Studio.
3. Let Visual Studio restore/build.
4. Press Ctrl+F5.
5. The home route opens the Dashboard.
6. Use the top navigation to open Weather Map, Analytics, Forecast and Recommendations.

DEFAULT LOCATION
Jalandhar, Punjab, India (31.2240, 75.7690).
Use the Dashboard search to choose another city. Selected coordinates are passed between pages.

FEATURES
- Live current weather via Open-Meteo
- Open-Meteo geocoding search
- Interactive Leaflet map with OpenStreetMap tiles
- Chart.js analytics: temperature, humidity, rainfall and precipitation probability
- Seven-day forecast and weather-based recommendations
- Responsive Bootstrap layout

IMPORTANT PROJECT SCOPE
Open-Meteo provides weather estimates/forecasts for coordinates. It does not provide farm soil-moisture or soil-pH sensor readings. Those are explicitly marked as requiring separate hardware/manual input. Recommendations are general guidance, not crop-specific agronomic advice.

No custom middleware is included in this final version. This avoids the earlier RequestLoggingMiddleware runtime exception.
