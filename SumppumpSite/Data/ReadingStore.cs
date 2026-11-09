using Microsoft.EntityFrameworkCore;

namespace SumppumpSite.Data;

// All database access. Uses a DbContext factory so the Blazor page and the API
// each get a short-lived context (a DbContext isn't safe to share between requests).
public class ReadingStore(IDbContextFactory<SumpDb> dbFactory)
{
    // A pump run = the level falls at least this much within 90 s.
    public const double PumpDropInches = 1.5;

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
        var rows = await RangeAsync(startMs, endMs);
        var bucketMs = PickBucket(endMs - startMs);
        return new ChartData(
            BucketSeconds: (int)(bucketMs / 1000),
            PumpRuns: CountPumpRuns(rows),
            Lowest: rows.Count > 0 ? rows.Min(r => r.Level) : null,
            Highest: rows.Count > 0 ? rows.Max(r => r.Level) : null,
            Points: Downsample(rows, bucketMs));
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

    static List<ChartPoint> Downsample(List<Reading> rows, long bucketMs)
    {
        if (bucketMs == 0)
            return rows.Select(r => new ChartPoint(r.Ts, r.Level, r.Level, r.Level)).ToList();

        return rows
            .GroupBy(r => r.Ts / bucketMs)   // rows are sorted, so the groups come out in time order
            .Select(g => new ChartPoint(
                g.Key * bucketMs + bucketMs / 2,
                Math.Round(g.Average(r => r.Level), 2),
                g.Min(r => r.Level),
                g.Max(r => r.Level)))
            .ToList();
    }

    static int CountPumpRuns(List<Reading> rows)
    {
        int runs = 0;
        long lastRun = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].Ts - lastRun < 180_000) continue;    // count each run once
            for (int j = i - 1; j >= 0 && rows[i].Ts - rows[j].Ts <= 90_000; j--)
            {
                if (rows[j].Level - rows[i].Level >= PumpDropInches)
                {
                    runs++;
                    lastRun = rows[i].Ts;
                    break;
                }
            }
        }
        return runs;
    }
}
