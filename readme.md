# QuickRoute — URL Shortener Service

A high-throughput URL shortening service built with **C#** and **ASP.NET Core**, featuring a custom LRU cache, Base62 encoding, structured logging, and a vintage-styled frontend. Built to demonstrate real backend engineering concepts with measurable, provable performance metrics.

---

![QuickRoute UI](/screenshot_ui.png)

---

## Table of Contents

- [What This Project Does](#what-this-project-does)
- [Tech Stack](#tech-stack)
- [Architecture Overview](#architecture-overview)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Getting Started](#getting-started)
- [How Each Part Works](#how-each-part-works)
  - [Base62 Encoding](#base62-encoding)
  - [LRU Cache](#lru-cache)
  - [The Service Layer](#the-service-layer)
  - [Structured Logging](#structured-logging)
- [API Endpoints](#api-endpoints)
- [Frontend](#frontend)
- [Load Testing with k6](#load-testing-with-k6)
- [Performance Results](#performance-results)
- [Database Setup](#database-setup)

---

## What This Project Does

QuickRoute takes a long URL like:
```
https://www.stackoverflow.com/questions/1642028/what-is-the-operator-in-c
```
And turns it into a short one like:
```
http://localhost:5111/5
```

When someone visits the short URL, they are instantly redirected to the original. The system keeps a cache of recently accessed URLs in memory so most requests never touch the database at all.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | C#, ASP.NET Core 9 (Minimal APIs) |
| Database | PostgreSQL via Entity Framework Core |
| Cache | Custom LRU implementation (HashMap + LinkedList) |
| Logging | Serilog with structured console output |
| Load Testing | k6 |
| Frontend | Vanilla HTML, CSS, JavaScript |

---

## Architecture Overview

Every incoming request flows through this pipeline:

```
Browser / Client
      ↓
ASP.NET Core (Kestrel HTTP Server)
      ↓
UrlService (business logic)
      ↓
LruCache ← check here first (microseconds)
      ↓ (on cache miss only)
PostgreSQL ← slow path (milliseconds)
      ↓
HTTP Response
```

The key insight is that the LRU cache sits between the service and the database. The first time a short code is accessed, it goes to the database. Every subsequent access for that same code is served entirely from memory — no database query needed.

---

## Project Structure

```
QuickRoute/
├── Cache/
│   └── LruCache.cs          ← Custom LRU implementation
├── Data/
│   └── AppDbContext.cs       ← Entity Framework DB context
├── Models/
│   └── ShortUrl.cs           ← Database model / table schema
├── Services/
│   └── UrlService.cs         ← Business logic: shorten, resolve, stats
├── wwwroot/
│   ├── index.html            ← Frontend UI
│   ├── style.css             ← Vintage newspaper styling
│   └── app.js                ← Frontend logic (fetch API calls)
├── appsettings.json          ← DB connection string, log levels
├── appsettings.Development.json
├── loadtest.js               ← k6 load test script
└── Program.cs                ← App entry point, DI registration, endpoints
```

---

## Prerequisites

Before building this, make sure you have:

- [.NET 9 SDK](https://dotnet.microsoft.com/download) — check with `dotnet --version`
- [PostgreSQL](https://www.postgresql.org/download/) — check with `psql --version`
- [k6](https://k6.io/docs/get-started/installation/) — check with `k6 version`
- VS Code with C# Dev Kit

---

## Getting Started

### 1. Clone and scaffold the project

```bash
mkdir QuickRoute
cd QuickRoute
dotnet new webapi --no-openapi
```

### 2. Install NuGet packages

```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 9.0.4
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.Console
```

> Note: Use version `9.0.4` of the Npgsql package. Version 10+ requires .NET 10.

### 3. Configure your database connection

Open `appsettings.json` and update the connection string:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=quickroute;Username=postgres;Password=yourpassword"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  }
}
```

### 4. Create the database table

Connect to PostgreSQL and run:

```sql
\c quickroute

CREATE TABLE IF NOT EXISTS "ShortUrls" (
    "Id" SERIAL PRIMARY KEY,
    "OriginalUrl" TEXT NOT NULL,
    "ShortCode" TEXT NOT NULL DEFAULT '',
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
    "HitCount" INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShortUrls_ShortCode" ON "ShortUrls" ("ShortCode");
```

> The unique index on `ShortCode` is critical for lookup performance. Without it, every redirect does a full table scan.

### 5. Run the application

```bash
dotnet run
```

Open `http://localhost:5111` in your browser to see the UI.

---

## How Each Part Works

### Base62 Encoding

Every URL gets an auto-incremented integer ID from the database. That integer is then encoded into a short alphanumeric string using Base62 — the character set `[0-9A-Za-z]`, giving 62 possible characters per position.

```csharp
private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

private static string Encode(long id)
{
    var sb = new StringBuilder();
    while (id > 0)
    {
        sb.Insert(0, Alphabet[(int)(id % 62)]);
        id /= 62;
    }
    return sb.ToString();
}
```

| Database ID | Base62 Code |
|---|---|
| 1 | `1` |
| 62 | `A` |
| 3844 | `AA` |
| 238,328 | `AAA` |

A 7-character Base62 string can represent over 3.5 trillion unique URLs. The algorithm runs in O(log n) time and completes in microseconds.

**Why not just use the ID directly?** You could, but Base62 produces shorter, cleaner strings as numbers grow. ID `10000` as a decimal is 5 characters; in Base62 it's `2Bi` — 3 characters.

---

### LRU Cache

LRU stands for Least Recently Used. It is a fixed-size cache that automatically evicts the entry that was accessed least recently when it becomes full. This is the right strategy for URL shorteners because traffic follows a power-law distribution — a small number of URLs receive the vast majority of clicks.

**Internal structure: HashMap + Doubly Linked List**

```
Most Recently Used                    Least Recently Used
         ↓                                     ↓
HEAD ←→ [github.com] ←→ [google.com] ←→ [reddit.com] ←→ TAIL
```

- The **HashMap** gives O(1) lookup by short code
- The **LinkedList** tracks recency — head is newest, tail is oldest
- On every cache **hit**, move that node to the head
- When the cache is **full**, evict from the tail

```csharp
public bool TryGet(TKey key, out TValue? value)
{
    lock (_lock)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _list.Remove(node);     // O(1) — we have direct node reference
            _list.AddFirst(node);   // Move to head (most recently used)
            value = node.Value.Value;
            Interlocked.Increment(ref _hits);
            return true;
        }
        value = default;
        Interlocked.Increment(ref _misses);
        return false;
    }
}
```

Both `TryGet` and `Put` run in **O(1)** time. The `lock` ensures thread safety under concurrent requests. `Interlocked.Increment` is used instead of `++` because it is an atomic CPU operation — safe without needing the lock.

**Why not just use `Dictionary`?** A plain dictionary has no size limit and no eviction strategy. It grows forever. The LRU cache stays at a fixed size and keeps the most useful entries.

---

### The Service Layer

`UrlService` is the brain of the application. It coordinates the cache, database, and encoding. The most important method is `ResolveAsync`:

```
Request for short code "5"
         ↓
Check LRU cache
    ↓ HIT                    ↓ MISS
Return immediately     Query PostgreSQL
(~0.2ms)              (~25ms)
                            ↓
                      Store in cache
                            ↓
                      Return result
```

By keeping the cache and database paths completely separate, cache hits are not slowed down by any database overhead. The `Stopwatch` wraps the entire resolution path so every request logs its own elapsed time.

The `HitCount` column in the database is incremented using **fire-and-forget**:

```csharp
_ = Task.Run(async () =>
{
    entry.HitCount++;
    await _db.SaveChangesAsync();
});
```

This means the redirect response is sent to the user immediately — the analytics write happens in the background and does not block the response.

---

### Structured Logging

Instead of plain `Console.WriteLine`, the project uses **Serilog** with structured properties:

```csharp
// Bad — plain string interpolation
Console.WriteLine($"Resolved {code} in {ms}ms");

// Good — structured properties
_logger.LogInformation(
    "Cache HIT | Code: {ShortCode} | ElapsedMs: {ElapsedMs} | HitRate: {HitRate:F1}%",
    shortCode, elapsedMs, _cache.HitRate);
```

Structured logging stores `ShortCode`, `ElapsedMs`, and `HitRate` as queryable fields, not just a string. In production this plugs into tools like Seq, Grafana, or Elastic without any code changes.

---

## API Endpoints

### `POST /shorten`
Accepts a long URL and returns a short code.

**Request:**
```json
{ "url": "https://www.github.com" }
```

**Response:**
```json
{
  "shortCode": "3",
  "shortUrl": "http://localhost:5111/3"
}
```

### `GET /{code}`
Resolves a short code and redirects (HTTP 302) to the original URL.

```
GET /3  →  302 Redirect  →  https://www.github.com
```

### `GET /stats`
Returns live cache performance metrics.

**Response:**
```json
{
  "cacheHits": 4925,
  "cacheMisses": 29,
  "hitRatePercent": 99.41
}
```

---

## Frontend

The frontend is a single-page vanilla HTML/CSS/JS application served as a static file by ASP.NET Core. It uses a vintage newspaper aesthetic — blackletter typography, ruled borders, monospace fonts, and aged paper tones.

![QuickRoute UI](/screenshot_ui.png)

The UI has three parts: a URL submission form, a result display with a copy button, and a live stats panel called "The Ledger" which shows cache hits, misses, and hit rate in real time.

Since the frontend uses `fetch()` to call the same ASP.NET Core server it is served from, no CORS configuration is needed — all requests go to the same origin.

---

## Load Testing with k6

k6 simulates multiple concurrent users hammering the redirect endpoint simultaneously. The test script picks a random short code from the seeded URLs on every iteration:

```javascript
import http from 'k6/http';
import { check } from 'k6';

export const options = {
    vus: 100,        // 100 virtual users
    duration: '30s', // run for 30 seconds
};

const codes = ['2', '3', '4', '5', '6', '7'];

export default function () {
    const code = codes[Math.floor(Math.random() * codes.length)];
    const res = http.get(`http://localhost:5111/${code}`, { redirects: 0 });
    check(res, { 'status is 302': (r) => r.status === 302 });
}
```

`redirects: 0` tells k6 not to follow the redirect to Google or GitHub. This ensures you are measuring only your server's response time, not the network time to the external destination.

**Run the load test:**
```bash
k6 run loadtest.js
```

**For performance testing (silences verbose logging first):**
```bash
dotnet run --configuration Release
k6 run loadtest.js
```

---

## Performance Results

![Load Test Results](/screenshot_loadtest.png)

| Metric | Result |
|---|---|
| Throughput | ~100 req/s (local Windows machine) |
| Cache hit rate | 99.41% |
| Cache hits | 4,925 |
| Cache misses | 29 |
| All checks passed | 100% (0 failures) |


**What the cache hit rate means:** Out of every 100 redirect requests, 99 are served entirely from memory without touching PostgreSQL. Only 1 in 100 goes to the database — the very first access of a given URL. Every subsequent access hits the cache.

Check live stats at any time:
```bash
curl http://localhost:5111/stats
```

---

## Database Setup

The project uses Entity Framework Core with PostgreSQL. The `ShortUrls` table schema:

| Column | Type | Notes |
|---|---|---|
| Id | SERIAL | Auto-incremented primary key, used for Base62 encoding |
| OriginalUrl | TEXT | The full long URL |
| ShortCode | TEXT | Base62 encoded Id, indexed for fast lookup |
| CreatedAt | TIMESTAMP | When the entry was created |
| HitCount | INTEGER | How many times this URL has been accessed |

The unique index on `ShortCode` is what makes redirects fast. PostgreSQL can jump directly to the matching row in O(log n) time instead of scanning every row.

`EnsureCreated()` in `Program.cs` attempts to create the schema on startup. If the database already has tables (for example, from another project), it skips creation — in that case, create the table manually using the SQL in the Getting Started section.

---

*Built with C# · ASP.NET Core 9 · PostgreSQL · k6 · Serilog*