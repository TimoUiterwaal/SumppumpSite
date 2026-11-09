using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SumppumpSite;
using SumppumpSite.Api;
using SumppumpSite.Components;
using SumppumpSite.Data;

// Helpers you run from a terminal in the project folder:
//   dotnet run -- hash-password   prints the value for Auth:PasswordHash
//   dotnet run -- new-api-key     prints a random value for Api:IngestKey (and the ESP32's secrets.yaml)
if (args is ["hash-password"]) { Tools.HashPassword(); return; }
if (args is ["new-api-key"]) { Tools.NewApiKey(); return; }

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

// Sign-in: one user, checked against Auth:Username and Auth:PasswordHash, remembered with a cookie for 30 days.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
	.AddCookie(o =>
	{
		o.LoginPath = "/login";
		o.Cookie.Name = "sump.auth";
		o.ExpireTimeSpan = TimeSpan.FromDays(30);
		o.SlidingExpiration = true;
		// API calls get a plain 401 instead of a redirect to the login page.
		o.Events.OnRedirectToLogin = ctx =>
		{
			if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
			else ctx.Response.Redirect(ctx.RedirectUri);
			return Task.CompletedTask;
		};
	});

// Every page and endpoint requires sign-in unless it's marked [AllowAnonymous] / .AllowAnonymous().
builder.Services.AddAuthorizationBuilder()
	.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

// At most 10 login page loads/attempts per 5 minutes from one IP address.
builder.Services.AddRateLimiter(o =>
{
	o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	o.OnRejected = (ctx, token) =>
	{
		ctx.HttpContext.Response.ContentType = "text/plain";
		return new ValueTask(ctx.HttpContext.Response.WriteAsync("Too many login attempts. Wait 5 minutes and try again.", token));
	};
	o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
		ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
});

// SQLite file. Locally it sits in the project folder; on Azure set DbFile to /home/data/sump.db
// (/home survives restarts and redeploys; the app's own folder doesn't).
var dbFile = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["DbFile"] ?? "sump.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbFile)!);
builder.Services.AddDbContextFactory<SumpDb>(o => o.UseSqlite($"Data Source={dbFile}"));
builder.Services.AddSingleton<ReadingStore>();

var app = builder.Build();

// Create the database and table on first run.
using (var db = app.Services.GetRequiredService<IDbContextFactory<SumpDb>>().CreateDbContext())
{
	db.Database.EnsureCreated();
	db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// No HTTPS redirect here: on Azure, turn on "HTTPS Only" instead. At home it runs on plain http.

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();   // CSS and scripts load on the login page too
app.MapSumpApi();
app.MapPost("/logout", async (HttpContext ctx) =>
{
	await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	return Results.Redirect("/login");
});
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.Run();
