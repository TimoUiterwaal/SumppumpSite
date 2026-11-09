# SumppumpSite

The ESP32 posts the sump level every 15 s (with a secret key) → the site saves it in SQLite → a chart page behind a login.

## 1. Make your password hash and the ESP32 key (once)

Stop the site in Visual Studio, then in a terminal in `SumppumpSite\SumppumpSite`:

```powershell
dotnet run -- hash-password    # type a password twice; copy the long hash it prints
dotnet run -- new-api-key      # copy the key it prints
```

For running on your desktop, save them as user secrets (stored in your Windows profile, not in the repo):

```powershell
dotnet user-secrets set "Auth:Username" "<your username>"
dotnet user-secrets set "Auth:PasswordHash" "<the hash>"
dotnet user-secrets set "Api:IngestKey" "<the key>"
```

Press F5. The site opens on the login page.

## 2. Create the Azure web app

1. Portal → Create a resource → **Web App**
   - Publish: Code · Runtime stack: **.NET 10** · Operating system: **Linux** · Region: Central US
   - Pricing plan: **Free F1** to start. It has a daily CPU quota; if the site stops with "quota exceeded", move to **Basic B1** (about $13/month).
2. Your web app → Settings → **Configuration** → General settings:
   - **Web sockets: On** (the live chart page uses them)
   - **HTTPS Only: On**
   - Always on: On (only offered on B1 and up)
3. Settings → **Environment variables** → App settings → add these, then Apply:

   | Name | Value |
   |---|---|
   | `DbFile` | `/home/data/sump.db` |
   | `Auth__Username` | your username |
   | `Auth__PasswordHash` | the hash |
   | `Api__IngestKey` | the key |
   | `TZ` | `America/Chicago` |
   | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |

   `DbFile` matters: `/home` survives restarts and redeploys, but the app's own folder is replaced on every publish.
4. Leave **Scale out** at 1 instance. SQLite can't be shared between instances.

## 3. Publish from Visual Studio

Right-click the **SumppumpSite** project → **Publish** → Azure → Azure App Service (Linux) → pick your web app → Finish → **Publish**.

Open the **Default domain** from the web app's Overview page (`https://…azurewebsites.net`) and log in.

## 4. Point the ESP32 at Azure

1. ESPHome Device Builder → **Secrets** (top right) → add `sump_api_key: "<the key>"`.
2. Replace the posting section of the sumppumpmonitor config with `esphome-snippet.yaml`, using your Default domain in the URL.
3. Install wirelessly. In the log, `Code: 204` means it's saving; `Code: 401` means the key on the ESP32 and the `Api__IngestKey` setting don't match.

## If SQLite acts up on Azure

App Service's `/home` is a network share, and SQLite's file locking doesn't always work on those. If the web app's **Log stream** shows `database is locked`, move to Azure SQL Basic (about $5/month):

1. Add the `Microsoft.EntityFrameworkCore.SqlServer` package.
2. In `Program.cs`, change `UseSqlite($"Data Source={dbFile}")` to `UseSqlServer(builder.Configuration.GetConnectionString("Sump"))` and delete the `PRAGMA journal_mode=WAL` line.
3. Add the database's connection string in the portal (Environment variables → Connection strings, name `Sump`).

Nothing else changes: `EnsureCreated` makes the table on SQL Server the same way.

## How the security works

- **Pages and the chart data** need the login cookie. Every page and endpoint is locked unless it's marked `[AllowAnonymous]` (the login, error and not-found pages, the CSS and scripts, and the ESP32's endpoint).
- **The ESP32's endpoint** (`POST /api/readings`) skips the login and requires the `X-Api-Key` header instead. With no key configured, nothing can post.
- **Login attempts** are limited to 10 per 5 minutes per IP address, with a 1 s delay after each wrong password.
- **Passwords** are stored only as a hash (ASP.NET Core Identity's `PasswordHasher`).

## Where things are

| File | What it does |
|---|---|
| `Program.cs` | Services, sign-in setup, the request pipeline |
| `Tools.cs` | The `hash-password` and `new-api-key` commands |
| `Components/Pages/Login.razor` | The login page |
| `Components/Pages/Home.razor` | The dashboard. `Marks` sets the lines across the chart |
| `wwwroot/js/sumpChart.js` | Draws the chart (Chart.js) and reports zooms back to Home.razor |
| `Api/SumpApi.cs` | The endpoints, including the ESP32 key check |
| `Data/ReadingStore.cs` | All database work: saving, downsampling, pump-run counting |

Endpoints (all but the first need the login cookie):

- `POST /api/readings` with header `X-Api-Key` and body `{"level": 5.2, "volts": 0.865}`
- `GET /api/readings?hours=24` or `?start=&end=` (Unix ms)
- `GET /api/latest`
- `GET /api/readings.csv?hours=168`

## Notes

- **Size:** about 2 million rows a year at 15 s, roughly 100 MB.
- **Recalibrating old data:** every row keeps the raw voltage. With the site stopped, run this in DB Browser for SQLite:
  ```sql
  UPDATE Readings SET Level = ROUND((Volts - 0.5786) / 0.0551 + 0.0, 2);   -- (volts - intercept) / slope + floor offset
  ```
- **Pump runs** counts each time the level falls at least 1.5 in within 90 s (`PumpDropInches` in `ReadingStore.cs`).
- **Schema changes:** `EnsureCreated` makes the table once and never changes it. If you add a column, delete the database or switch to EF migrations.
- `Pages/Counter.razor` and `Pages/Weather.razor` are leftovers from the template and safe to delete.
