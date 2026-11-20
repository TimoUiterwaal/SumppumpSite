using Microsoft.EntityFrameworkCore;

namespace SumppumpSite.Data;

// All database access. Uses a DbContext factory so the Blazor page and the API
// each get a short-lived context (a DbContext isn't safe to share between requests).
public class ReadingStore(IDbContextFactory<SumpDb> dbFactory)
{
    // A pump run = the level falls at least this much within 90 s.
    public const double PumpDropInches = 1.5;
    const long PumpWindowMs = 90_000;      // how far back to look for the higher level
    const long PumpRunGapMs = 180_000;     // count each run once

    public async Task AddAsync(double level, double volts)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Readings.Add(new Reading
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level = Math.Round(level, 2),
            Volts = Math.Round(volts, 4),
        });
        await db.SaveChangesAsync();
    }

    public async Task<Reading?> LatestAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Readings.AsNoTracking().OrderByDescending(r => r.Ts).FirstOrDefaultAsync();
    }

    public async Task<List<Reading>> RangeAsync(long startMs, long endMs)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Readings.AsNoTracking()
            .Where(r => r.Ts >= startMs && r.Ts <= endMs)
            .OrderBy(r => r.Ts)
            .ToListAsync();
    }

    // Everything the chart needs for one time window.
    public async Task<ChartData> ChartAsync(long startMs, long endMs)
    {
        var bucketMs = PickBucket(endMs - startMs);
        await using var db = await dbFactory.CreateDbContextAsync();
        var points = await PointsAsync(db, startMs, endMs, bucketMs);
        var (runs, lastRun) = await PumpRunsAsync(db, startMs, endMs, countAfter: long.MinValue, lastRun: 0);
        return new ChartData(
            BucketSeconds: (int)(bucketMs / 1000),
            PumpRuns: runs,
            Lowest: points.Count > 0 ? points.Min(p => p.Lo) : null,
            Highest: points.Count > 0 ? points.Max(p => p.Hi) : null,
            Points: points,
            LastTs: await LastTsAsync(db, startMs, endMs),
            LastPumpRunTs: lastRun > 0 ? lastRun : null);
    }

    // Only what's new since a live chart last loaded (sinceTs = the newest reading it has).
    // Sends a few points instead of the whole window. For bucketed charts the last bucket was
    // probably still filling, so it's sent again and replaces the old one.
    // Lowest/Highest cover only the new readings; the page merges them with what it has.
    public async Task<ChartUpdate> ChartUpdateAsync(long sinceTs, long endMs, int bucketSeconds, long? lastPumpRunTs)
    {
        long bucketMs = bucketSeconds * 1000L;
        long from = bucketMs == 0 ? sinceTs + 1 : sinceTs / bucketMs * bucketMs;
        await using var db = await dbFactory.CreateDbContextAsync();
        var points = await PointsAsync(db, from, endMs, bucketMs);
        var (runs, lastRun) = await PumpRunsAsync(db, sinceTs - PumpWindowMs, endMs, countAfter: sinceTs, lastRun: lastPumpRunTs ?? 0);
        var newOnly = db.Readings.Where(r => r.Ts > sinceTs && r.Ts <= endMs);
        return new ChartUpdate(
            Points: points,
            NewPumpRuns: runs,
            Lowest: await newOnly.MinAsync(r => (double?)r.Level),
            Highest: await newOnly.MaxAsync(r => (double?)r.Level),
            LastTs: await LastTsAsync(db, sinceTs + 1, endMs) ?? sinceTs,
            LastPumpRunTs: lastRun > 0 ? lastRun : null);
    }

    // Turns ?hours= or ?start=&end= (Unix ms) into a window. Default: the last 24 h.
    public static (long Start, long End) Window(long? start, long? end, double? hours)
    {
        long endMs = end ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startMs = start ?? endMs - (long)(Math.Clamp(hours ?? 24, 0.1, 24 * 400) * 3_600_000);
        return (startMs, Math.Max(endMs, startMs + 1000));
    }

    // Up to 6 h: every reading (at most 1,440). Longer: one point per bucket, at most ~2,000 points.
    static long PickBucket(long spanMs)
    {
        if (spanMs <= 6 * 3_600_000L) return 0;
        long[] steps = [60_000, 300_000, 900_000, 1_800_000, 3_600_000, 10_800_000, 21_600_000, 86_400_000];
        foreach (var s in steps)
            if (spanMs / s <= 2_000) return s;
        return steps[^1];
    }

    static Task<long?> LastTsAsync(SumpDb db, long startMs, long endMs) =>
        db.Readings.Where(r => r.Ts >= startMs && r.Ts <= endMs).MaxAsync(r => (long?)r.Ts);

    // Bucket averages are worked out by SQLite (GROUP BY), so only the ~2,000 chart points
    // leave the database instead of every reading in the window.
    static async Task<List<ChartPoint>> PointsAsync(SumpDb db, long startMs, long endMs, long bucketMs)
    {
        var range = db.Readings.AsNoTracking().Where(r => r.Ts >= startMs && r.Ts <= endMs);
        if (bucketMs == 0)
            return await range.OrderBy(r => r.Ts)
                .Select(r => new ChartPoint(r.Ts, r.Level, r.Level, r.Level))
                .ToListAsync();

        var buckets = await range
            .GroupBy(r => r.Ts / bucketMs)
            .Select(g => new { Key = g.Key, V = g.Average(r => r.Level), Lo = g.Min(r => r.Level), Hi = g.Max(r => r.Level) })
            .OrderBy(b => b.Key)
            .ToListAsync();
        return buckets.Select(b => new ChartPoint(b.Key * bucketMs + bucketMs / 2, Math.Round(b.V, 2), b.Lo, b.Hi)).ToList();
    }

    // Reads (time, level) pairs one at a time instead of loading the whole window into a list.
    // Only runs found after countAfter are counted; earlier rows just give the 90 s of history.
    static async Task<(int Runs, long LastRun)> PumpRunsAsync(SumpDb db, long startMs, long endMs, long countAfter, long lastRun)
    {
        int runs = 0;
        var recent = new Queue<(long Ts, double Level)>();
        var rows = db.Readings.AsNoTracking()
            .Where(r => r.Ts >= startMs && r.Ts <= endMs)
            .OrderBy(r => r.Ts)
            .Select(r => new { r.Ts, r.Level })
            .AsAsyncEnumerable();
        await foreach (var r in rows)
        {
            while (recent.Count > 0 && r.Ts - recent.Peek().Ts > PumpWindowMs) recent.Dequeue();
            if (r.Ts > countAfter && r.Ts - lastRun >= PumpRunGapMs
                && recent.Any(p => p.Level - r.Level >= PumpDropInches))
            {
                runs++;
                lastRun = r.Ts;
            }
            recent.Enqueue((r.Ts, r.Level));
        }
        return (runs, lastRun);
    }
}
