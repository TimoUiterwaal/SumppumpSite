using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SumppumpSite.Data;

namespace SumppumpSite.Api;

public static class SumpApi
{
    public record ReadingIn(double? Level, double? Volts);

    public static void MapSumpApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // The ESP32 posts {"level": 6.2, "volts": 0.931} every 15 s with an X-Api-Key header.
        // It doesn't log in, so this endpoint skips sign-in and checks the key instead.
        // Reads the body itself: ESPHome may not send a JSON Content-Type header, and a
        // ReadingIn parameter would make ASP.NET reject that with 415 Unsupported Media Type.
        api.MapPost("/readings", async (HttpRequest request, ReadingStore store, IConfiguration config) =>
        {
            if (!ApiKeyMatches(request, config["Api:IngestKey"]))
                return Results.Json(new { error = "missing or wrong X-Api-Key header" }, statusCode: StatusCodes.Status401Unauthorized);

            ReadingIn? r;
            try { r = await JsonSerializer.DeserializeAsync<ReadingIn>(request.Body, JsonSerializerOptions.Web); }
            catch (JsonException) { r = null; }

            if (r is not { Level: double level, Volts: double volts }
                || !double.IsFinite(level) || !double.IsFinite(volts))
                return Results.BadRequest("expected {\"level\": number, \"volts\": number}");

            await store.AddAsync(level, volts);
            return Results.NoContent();
        }).AllowAnonymous();

        // Same data the chart uses: ?hours=24, or ?start=<unix ms>&end=<unix ms>
        api.MapGet("/readings", async (long? start, long? end, double? hours, ReadingStore store) =>
        {
            var (s, e) = ReadingStore.Window(start, end, hours);
            return Results.Ok(await store.ChartAsync(s, e));
        });

        api.MapGet("/latest", async (ReadingStore store) =>
            await store.LatestAsync() is { } r ? Results.Ok(r) : Results.NoContent());

        // Every reading in a window, for Excel: ?hours=168 or ?start=&end=
        api.MapGet("/readings.csv", async (long? start, long? end, double? hours, ReadingStore store) =>
        {
            var (s, e) = ReadingStore.Window(start, end, hours);
            var sb = new StringBuilder("time,level_in,volts\n");
            foreach (var x in await store.RangeAsync(s, e))
            {
                sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(x.Ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .Append(',').Append(x.Level.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(x.Volts.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "sump-readings.csv");
        });
    }

    // No key configured = nobody can post. Constant-time compare so the key can't be guessed by timing.
    static bool ApiKeyMatches(HttpRequest request, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        var given = request.Headers["X-Api-Key"].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }
}
