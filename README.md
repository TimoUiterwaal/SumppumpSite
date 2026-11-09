# SumppumpSite

A small website for watching the water level in a sump pit.

An ESP32 running [ESPHome](https://esphome.io) measures the level and posts it to the site every 15 seconds. The site saves each reading in SQLite and shows a live chart behind a login.

```
ESP32 (ESPHome) ──POST /api/readings + X-Api-Key──▶ ASP.NET Core site ──▶ SQLite
                                                          │
                                         browser ◀── login + live chart
```

**What you get**

- Current level, with a warning when the sensor stops reporting
- Chart for 1 h / 6 h / 24 h / 7 d / 30 d, with drag or pinch to zoom
- Lowest and highest level, and how many times the pump ran, in the visible window
- CSV download of the raw readings
- A single-user login, and an API key for the ESP32

Built with .NET 10, Blazor Server, Entity Framework Core (SQLite) and Chart.js.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: a Visual Studio version that supports .NET 10. Any editor with the `dotnet` CLI also works.
- For real data: an ESP32 with ESPHome and a level sensor (see [Connect the ESP32](#connect-the-esp32)). You can try the site without one.

## Quick start (local)

### 1. Clone and go to the project folder

```bash
git clone https://github.com/TimoUiterwaal/SumppumpSite.git
```

```bash
cd SumppumpSite/SumppumpSite
```

### 2. Make a password hash and an API key

```bash
dotnet run -- hash-password
```

Type a password twice and copy the hash it prints.

```bash
dotnet run -- new-api-key
```

Copy the key it prints. The ESP32 sends it with every reading.

### 3. Save them as user secrets

User secrets are stored in your user profile, outside the repo, so they never get committed.

```bash
dotnet user-secrets set "Auth:Username" "<your username>"
```

```bash
dotnet user-secrets set "Auth:PasswordHash" "<the hash>"
```

```bash
dotnet user-secrets set "Api:IngestKey" "<the key>"
```

### 4. Run it

```bash
dotnet run
```

Open <http://localhost:5026> and log in. The database file `sump.db` is created on first run.

The site listens on all network interfaces (`0.0.0.0:5026`), so an ESP32 on your home network can reach it at `http://<your PC's IP>:5026`.

### 5. Send a test reading (no ESP32 needed)

PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5026/api/readings -Headers @{ "X-Api-Key" = "<the key>" } -ContentType "application/json" -Body '{"level": 6.2, "volts": 0.931}'
```

bash / macOS / Linux:

```bash
curl -X POST http://localhost:5026/api/readings -H "X-Api-Key: <the key>" -H "Content-Type: application/json" -d '{"level": 6.2, "volts": 0.931}'
```

No output means it worked (HTTP 204). The reading appears on the chart within 15 seconds.

## Configuration

Settings come from `appsettings.json`, user secrets, or environment variables. In environment variables, use `__` instead of `:` (for example `Auth__PasswordHash`).

| Setting | What it does | Default |
|---|---|---|
| `Auth:Username` | Login username (not case-sensitive) | *empty, login disabled* |
| `Auth:PasswordHash` | Hash from `dotnet run -- hash-password` | *empty, login disabled* |
| `Api:IngestKey` | Key the ESP32 must send in `X-Api-Key` | *empty, nothing can post* |
| `DbFile` | SQLite file path, relative to the project folder or absolute | `sump.db` |
| `Esp32Url` | Where the "ESP32 page" link in the menu points | `http://192.168.1.196` |

Never put real values for the first three in `appsettings.json`. Use user secrets locally and environment variables or app settings when hosted.

## Connect the ESP32

The site doesn't depend on a particular sensor. It only needs the ESP32 to post JSON like this every 15 seconds:

```json
{ "level": 6.2, "volts": 0.931 }
```

- `level`: water level in inches (whatever your calibration produces)
- `volts`: the raw sensor voltage. It's stored so you can recalibrate old readings later.

[esphome-snippet.yaml](esphome-snippet.yaml) is a ready-made ESPHome block that does this. It expects two sensors in your config with the ids `sump_level` (inches) and `probe_v` (volts). To use it:

1. In ESPHome, add the key to your secrets: `sump_api_key: "<the key>"`.
2. Paste the snippet at the bottom of your device's config (top level, not indented).
3. Change the `url` to your site's address, for example `http://<your PC's IP>:5026/api/readings` or `https://<your-app>.azurewebsites.net/api/readings`.
4. Install the update. In the ESPHome log, `Code: 204` means readings are being saved. `Code: 401` means the key on the ESP32 doesn't match `Api:IngestKey`.

## Deploy to Azure App Service

Any host that runs ASP.NET Core works. These steps are for Azure App Service on Linux.

1. Create a **Web App**: Publish **Code**, runtime **.NET 10**, OS **Linux**. The **Free F1** plan works to start; if you hit "quota exceeded", move to **Basic B1**.
2. Under **Configuration → General settings**, turn on **Web sockets** (the live chart needs them) and **HTTPS Only**. Turn on **Always on** if your plan offers it.
3. Under **Environment variables → App settings**, add:

   | Name | Value |
   |---|---|
   | `Auth__Username` | your username |
   | `Auth__PasswordHash` | the hash |
   | `Api__IngestKey` | the key |
   | `DbFile` | `/home/data/sump.db` |
   | `TZ` | your time zone, e.g. `America/Chicago` |
   | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |

   - `DbFile` must be under `/home`, which survives restarts and redeploys. The app's own folder is replaced on every publish.
   - `TZ` sets the time shown in CSV downloads.
   - `ASPNETCORE_FORWARDEDHEADERS_ENABLED` lets the login rate limit see each visitor's real IP address.
4. Keep **Scale out** at 1 instance. SQLite can't be shared between instances.
5. Publish: in Visual Studio, right-click the project → **Publish** → Azure App Service (Linux). From the CLI, run `dotnet publish -c Release` and deploy the output folder.

Publish profiles (`*.pubxml`) are in `.gitignore` because they contain your Azure subscription details.

**If you see `database is locked`:** `/home` on App Service is a network share, and SQLite's file locking doesn't always work there. Switch to Azure SQL: add the `Microsoft.EntityFrameworkCore.SqlServer` package, replace `UseSqlite(...)` in `Program.cs` with `UseSqlServer(builder.Configuration.GetConnectionString("Sump"))`, remove the `PRAGMA journal_mode=WAL` line, and add a `Sump` connection string in the portal. `EnsureCreated` creates the table there the same way.

## How the security works

- Every page and endpoint requires login unless it's marked `[AllowAnonymous]`. The exceptions are the login, error and not-found pages, static files, and the ESP32's `POST /api/readings`.
- `POST /api/readings` requires the `X-Api-Key` header instead of a login, compared in constant time. If no key is configured, nothing can post.
- Login attempts are limited to 10 per 5 minutes per IP address, with a 1-second delay after each wrong password.
- Only the password hash is stored, using ASP.NET Core Identity's `PasswordHasher`. The login cookie lasts 30 days.

## API

All endpoints except the first need the login cookie.

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/readings` | Save a reading. Header `X-Api-Key`, body `{"level": 5.2, "volts": 0.865}` |
| `GET` | `/api/readings?hours=24` | Chart data for a window. Or use `?start=&end=` in Unix ms |
| `GET` | `/api/latest` | The newest reading (204 if there are none) |
| `GET` | `/api/readings.csv?hours=168` | Every reading in a window, as CSV |

## Project layout

| Path | What it does |
|---|---|
| `SumppumpSite/Program.cs` | Services, login setup, rate limiting, the request pipeline |
| `SumppumpSite/Tools.cs` | The `hash-password` and `new-api-key` commands |
| `SumppumpSite/Api/SumpApi.cs` | The API endpoints and the ESP32 key check |
| `SumppumpSite/Data/ReadingStore.cs` | All database work: saving, downsampling, pump-run counting |
| `SumppumpSite/Components/Pages/Home.razor` | The dashboard page |
| `SumppumpSite/Components/Pages/Login.razor` | The login page |
| `SumppumpSite/wwwroot/js/sumpChart.js` | Draws the chart with Chart.js and reports zooms back to the page |
| `esphome-snippet.yaml` | ESPHome block that posts readings to the site |

## Customizing

- **Lines across the chart** (for example where the pump starts): edit `Marks` in `Home.razor`.
- **Pump-run detection**: a run is counted when the level drops at least 1.5 in within 90 s. Change `PumpDropInches` in `ReadingStore.cs`.
- **Recalibrating old data**: every row keeps the raw voltage, so you can recompute levels. With the site stopped, run something like this in a SQLite tool such as DB Browser for SQLite:

  ```sql
  UPDATE Readings SET Level = ROUND((Volts - 0.5786) / 0.0551, 2);  -- (volts - intercept) / slope
  ```

- **Adding a column**: `EnsureCreated` creates the table once and never changes it. Delete the database file or switch to EF Core migrations.
- `Pages/Counter.razor` and `Pages/Weather.razor` are leftovers from the project template and safe to delete.

## Good to know

- At one reading every 15 s, the database grows by about 2 million rows (roughly 100 MB) a year.
- Charts up to 6 hours show every reading. Longer windows are averaged into buckets so the chart stays at about 2,000 points; lowest and highest still use the raw readings.
