using Microsoft.EntityFrameworkCore;
using QuickRoute.Cache;
using QuickRoute.Data;
using QuickRoute.Models;
using System.Diagnostics;

namespace QuickRoute.Services;

public class UrlService
{
    private readonly AppDbContext _db;
    private readonly LruCache<string, string> _cache;
    private readonly ILogger<UrlService> _logger;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public UrlService(AppDbContext db, LruCache<string, string> cache, ILogger<UrlService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    // --- Base62 Encoding ---
    private static string Encode(long id)
    {
        var sb = new System.Text.StringBuilder();
        while (id > 0)
        {
            sb.Insert(0, Alphabet[(int)(id % 62)]);
            id /= 62;
        }
        return sb.ToString();
    }

    // --- Shorten a URL ---
    public async Task<string> ShortenAsync(string originalUrl)
    {
        // Check if this URL was already shortened
        var existing = await _db.ShortUrls
            .FirstOrDefaultAsync(u => u.OriginalUrl == originalUrl);

        if (existing != null)
        {
            _logger.LogInformation("URL already exists: {ShortCode}", existing.ShortCode);
            return existing.ShortCode;
        }

        // Save first to get the auto-incremented Id
        var entry = new ShortUrl { OriginalUrl = originalUrl };
        _db.ShortUrls.Add(entry);
        await _db.SaveChangesAsync();

        // Now encode the Id to generate ShortCode
        entry.ShortCode = Encode(entry.Id);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created short code {ShortCode} for {OriginalUrl}",
            entry.ShortCode, originalUrl);

        return entry.ShortCode;
    }

    // --- Resolve a short code to original URL ---
    public async Task<(string? Url, bool CacheHit, long ElapsedMs)> ResolveAsync(string shortCode)
    {
        var sw = Stopwatch.StartNew();

        // 1. Check cache first
        if (_cache.TryGet(shortCode, out var cachedUrl))
        {
            sw.Stop();
            _logger.LogInformation(
                "Cache HIT | Code: {ShortCode} | ElapsedMs: {ElapsedMs} | HitRate: {HitRate:F1}%",
                shortCode, sw.ElapsedMilliseconds, _cache.HitRate);
            return (cachedUrl, true, sw.ElapsedMilliseconds);
        }

        // 2. Cache miss — go to DB
        var entry = await _db.ShortUrls
            .FirstOrDefaultAsync(u => u.ShortCode == shortCode);

        sw.Stop();

        if (entry is null)
        {
            _logger.LogWarning("Code not found: {ShortCode}", shortCode);
            return (null, false, sw.ElapsedMilliseconds);
        }

        // 3. Store in cache for next time
        _cache.Put(shortCode, entry.OriginalUrl);

        // 4. Increment hit count — fire and forget, don't block the response
        _ = Task.Run(async () =>
        {
            try
            {
                entry.HitCount++;
                await _db.SaveChangesAsync();
            }
            catch { /* don't crash the request if analytics fail */ }
        });

        _logger.LogInformation(
            "Cache MISS | Code: {ShortCode} | ElapsedMs: {ElapsedMs} | HitRate: {HitRate:F1}%",
            shortCode, sw.ElapsedMilliseconds, _cache.HitRate);

        return (entry.OriginalUrl, false, sw.ElapsedMilliseconds);
    }

    // --- Get cache stats ---
    public (int Hits, int Misses, double HitRate) GetCacheStats()
        => (_cache.Hits, _cache.Misses, _cache.HitRate);
}