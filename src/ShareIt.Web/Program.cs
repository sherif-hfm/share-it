using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Infrastructure;
using ShareIt.Infrastructure.Persistence;
using ShareIt.Web.Authentication;
using ShareIt.Web.Components;
using ShareIt.Web.Endpoints;
using ShareIt.Web.Services;
using ShareIt.Web.WebDav;

var builder = WebApplication.CreateBuilder(args);
var allowHttp = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing") ||
    builder.Configuration.GetValue<bool>("ShareIt:AllowHttp");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
var limits = builder.Configuration.GetSection("Limits").Get<ShareItLimits>() ?? new();
var configuredPath = builder.Configuration["ShareIt:DataPath"] ?? ".runtime";
var dataPath = Path.GetFullPath(configuredPath, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionChangeNotifier>();
builder.Services.AddSingleton<ISessionChangePublisher>(sp => sp.GetRequiredService<SessionChangeNotifier>());
builder.Services.AddShareItInfrastructure(dataPath, builder.Configuration["ShareIt:PinPepperFile"]);
builder.Services.AddSingleton<CredentialGuard>();
builder.Services.AddScoped<WebDavResourceProvider>();
builder.Services.AddSingleton<CircuitRegistry>();
builder.Services.AddScoped<CircuitHandler, CircuitBudget>();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    o.DetailedErrors = builder.Environment.IsDevelopment();
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
    o.DisconnectedCircuitMaxRetained = 50;
}).AddHubOptions(o => o.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDataProtection().SetApplicationName("ShareIt").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddAuthentication("ShareIt")
    .AddPolicyScheme("ShareIt", null, o => o.ForwardDefaultSelector = ctx =>
        WebDavProtocol.IsWebDav(ctx) || (ctx.Request.Path.StartsWithSegments("/api/v1") && ctx.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            ? BrowserIdentity.BasicScheme : BrowserIdentity.CookieScheme)
    .AddCookie(BrowserIdentity.CookieScheme, o =>
    {
        o.Cookie.Name = "shareit.browser";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = allowHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(24);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, SessionBasicHandler>(BrowserIdentity.BasicScheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust only an explicitly configured proxy (or loopback). Never trust arbitrary X-Forwarded-For.
    if (System.Net.IPAddress.TryParse(builder.Configuration["ShareIt:TrustedProxy"], out var proxy)) o.KnownProxies.Add(proxy);
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = limits.MaxFileBytes + 128 * 1024);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter($"{(WebDavProtocol.IsWebDav(ctx) ? "dav" : "http")}:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}", _ => new()
        { PermitLimit = WebDavProtocol.IsWebDav(ctx) ? Math.Max(1, limits.WebDavRequestsPerMinute) : 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("create", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        { PermitLimit = 10, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (WebDavProtocol.IsWebDav(ctx.HttpContext))
            await WebDavProtocol.ErrorAsync(ctx.HttpContext, 429, "try_later", "Too many requests. Please try again later.");
        else await ctx.HttpContext.Response.WriteAsJsonAsync(new { detail = "Too many requests. Please try again later.", code = "try_later" }, ct);
    };
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShareItDbContext>>().CreateDbContextAsync();
    await using (db)
    {
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
if (!allowHttp) app.UseHttpsRedirection();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (!ctx.Request.Path.StartsWithSegments("/css") && !ctx.Request.Path.StartsWithSegments("/fonts") && !ctx.Request.Path.StartsWithSegments("/brand"))
        ctx.Response.Headers.CacheControl = "no-store";
    var origin = ctx.Request.Headers.Origin.ToString();
    if (ctx.Request.Path.StartsWithSegments("/_blazor") && origin.Length > 0 &&
        !string.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase))
    { ctx.Response.StatusCode = 403; return; }
    try { await next(ctx); }
    catch (ShareItException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.Status;
        if (ex.Status == 429 && ex.RetryAfterSeconds is { } retryAfter)
            ctx.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (WebDavProtocol.IsWebDav(ctx)) await WebDavProtocol.ErrorAsync(ctx, ex.Status, ex.Code, ex.Message);
        else await ctx.Response.WriteAsJsonAsync(new { title = "Request could not be completed", detail = ex.Message, code = ex.Code });
    }
    catch (AntiforgeryValidationException) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { detail = "Refresh this page and try again.", code = "csrf" }); }
    catch (InvalidDataException) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { detail = "The upload is invalid or exceeds its limit.", code = "upload" }); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapSessionEndpoints();
app.MapTextEndpoints();
app.MapFileEndpoints();
app.MapWebDavEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
