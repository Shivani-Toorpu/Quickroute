using Microsoft.EntityFrameworkCore;
using QuickRoute.Cache;
using QuickRoute.Data;
using QuickRoute.Services;
using Serilog;

// --- Serilog Setup ---
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// --- Database ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- LRU Cache (Singleton — lives for entire app lifetime) ---
builder.Services.AddSingleton<LruCache<string, string>>(_ => new LruCache<string, string>(1000));

// --- Url Service (Scoped — one per request) ---
builder.Services.AddScoped<UrlService>();

var app = builder.Build();

// --- Auto create table if it doesn't exist ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- Static Files ---
app.UseStaticFiles();

// --- Endpoints ---

app.MapGet("/", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html"));
});

app.MapGet("/style.css", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/css";
    await ctx.Response.SendFileAsync(
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "style.css"));
});

app.MapGet("/app.js", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "application/javascript";
    await ctx.Response.SendFileAsync(
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "app.js"));
});

app.MapPost("/shorten", async (UrlRequest request, UrlService svc) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest("URL is required");

    var shortCode = await svc.ShortenAsync(request.Url);
    return Results.Ok(new { shortCode, shortUrl = $"http://localhost:5111/{shortCode}" });
});

app.MapGet("/stats", (UrlService svc) =>
{
    var (hits, misses, hitRate) = svc.GetCacheStats();
    return Results.Ok(new
    {
        cacheHits = hits,
        cacheMisses = misses,
        hitRatePercent = Math.Round(hitRate, 2)
    });
});

app.MapGet("/{code}", async (string code, UrlService svc) =>
{
    if (code.Contains('.'))
        return Results.NotFound();

    var (url, cacheHit, elapsedMs) = await svc.ResolveAsync(code);

    if (url is null)
        return Results.NotFound("Short code not found");

    return Results.Redirect(url);
});

app.Run();

record UrlRequest(string Url);