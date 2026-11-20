using ClimateMonitoringSystem.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Check if running in automated verification test mode
if (args.Contains("--test"))
{
    var exitCode = await AppTestRunner.RunAllTestsAsync();
    Environment.Exit(exitCode);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Configure port for Render or local hosting
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Ensure DataProtection keys are stored locally within the application directory
var keysFolder = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
if (!Directory.Exists(keysFolder))
{
    try { Directory.CreateDirectory(keysFolder); } catch { }
}
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysFolder))
    .SetApplicationName("AgroSense");

// Add MVC services
builder.Services.AddControllersWithViews();

// Register in-memory cache for weather & geocoding data
builder.Services.AddMemoryCache();

// Register distributed cache and session for location persistence
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Register User Location Service for centralized synchronization
builder.Services.AddScoped<IUserLocationService, UserLocationService>();

// Register typed HttpClient for WeatherService with custom headers and timeout
builder.Services.AddHttpClient<WeatherService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("User-Agent", "AgroSense-ClimateMonitoring/1.0");
});

var app = builder.Build();

// Forwarded headers for reverse proxies (Render, Cloudflare, etc.)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Climate/Dashboard");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

// Enable session state
app.UseSession();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Climate}/{action=Dashboard}/{id?}");

app.Run();
