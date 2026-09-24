using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Infrastructure.BackgroundJobs;

namespace ShareIt.Web.Tests;

public sealed class AppFactory(TimeProvider? clock = null) : WebApplicationFactory<Program>
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shareit-web-tests", Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ShareIt:DataPath", root);
        builder.ConfigureServices(services =>
        {
            if (clock != null) services.AddSingleton(clock);
            foreach (var item in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(CleanupWorker)).ToArray()) services.Remove(item);
        });
    }
    public HttpClient Browser() => CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    public async Task<CreatedSession> CreateSession(HttpClient client)
    {
        await Csrf(client);
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new { minutes = 60 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedSession>())!;
    }
    public static async Task Csrf(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/antiforgery");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", response.GetProperty("token").GetString());
    }
    public async Task<Caller> Owner(Guid id) => new((await Services.GetRequiredService<IShareItPersistence>().FindAsync(id))!.Grants.First().BrowserId);
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shareit-web-tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
    }
}

public class ApiTests
{
    private static void Basic(HttpClient client, string code, string pin) => client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(code + ":" + pin)));

    [Fact]
    public async Task Browser_can_create_and_a_second_browser_joins_without_an_account()
    {
        await using var app = new AppFactory();
        using var first = app.Browser(); using var second = app.Browser();
        var session = await app.CreateSession(first);
        Assert.Matches("^[a-z0-9]{3}-[a-z0-9]{3}$", session.Code); Assert.Matches("^[0-9]{4}$", session.Pin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync($"/api/v1/sessions/{session.Code}")).StatusCode);
        await AppFactory.Csrf(second);
        (await second.PostAsJsonAsync("/api/v1/join", new { session.Code, session.Pin })).EnsureSuccessStatusCode();
        (await second.GetAsync($"/api/v1/sessions/{session.Code}")).EnsureSuccessStatusCode();
        Assert.Equal(2, (await app.Services.GetRequiredService<IShareItPersistence>().FindAsync(session.Id))!.Grants.Count);
    }

    [Fact]
    public async Task Raw_text_preserves_bytes_and_basic_cannot_cross_sessions()
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var terminal = app.Browser();
        var session = await app.CreateSession(browser);
        const string text = "  printf 'hello'\r\n\t# مرحبا 😀\n";
        await app.Services.GetRequiredService<TextCardService>().SaveAsync(session.Id, await app.Owner(session.Id), null, null, "script", text, "bash");
        Basic(terminal, session.Code, session.Pin);
        var response = await terminal.GetAsync($"/api/v1/sessions/{session.Code}/texts/1/raw");
        Assert.Equal(Encoding.UTF8.GetBytes(text), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var other = await app.CreateSession(browser);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.GetAsync($"/api/v1/sessions/{other.Code}")).StatusCode);
    }

    [Fact]
    public async Task Missing_antiforgery_token_rejects_mutation()
    {
        await using var app = new AppFactory(); using var browser = app.Browser();
        var response = await browser.PostAsJsonAsync("/api/v1/sessions", new { minutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_bad_pins_are_throttled_before_hashing()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var terminal = app.Browser();
        var session = await app.CreateSession(browser);
        var wrong = session.Pin == "0000" ? "0001" : "0000";
        Basic(terminal, session.Code, wrong);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await terminal.GetAsync($"/api/v1/sessions/{session.Code}")).StatusCode);
        var blocked = await terminal.GetAsync($"/api/v1/sessions/{session.Code}");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); Assert.NotNull(blocked.Headers.RetryAfter);
        Assert.InRange(blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 295, 300);
        (await browser.GetAsync($"/api/v1/sessions/{session.Code}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Files_stream_as_attachments_and_closed_sessions_block_existing_grants()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var terminal = app.Browser();
        var session = await app.CreateSession(browser);
        await AppFactory.Csrf(browser);
        using var upload = new MultipartFormDataContent();
        byte[] bytes = [0, 1, 255, 42, 10];
        upload.Add(new ByteArrayContent(bytes), "file", "../../config.bin");
        browser.DefaultRequestHeaders.Add("X-File-Size", "5");
        var uploaded = await browser.PostAsync($"/api/v1/sessions/{session.Code}/files", upload);
        uploaded.EnsureSuccessStatusCode();
        Basic(terminal, session.Code, session.Pin);
        var result = await terminal.GetAsync($"/api/v1/sessions/{session.Code}/files/1");
        result.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await result.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", result.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("config.bin", result.Content.Headers.ContentDisposition.FileNameStar);
        await app.Services.GetRequiredService<SessionService>().EndAsync(session.Id, await app.Owner(session.Id), false);
        Assert.Equal(HttpStatusCode.Gone, (await browser.GetAsync($"/api/v1/sessions/{session.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await terminal.GetAsync($"/api/v1/sessions/{session.Code}")).StatusCode);
    }

    [Fact]
    public async Task Pin_hash_does_not_store_the_pin_and_is_bound_to_server_pepper()
    {
        await using var app = new AppFactory(); using var browser = app.Browser();
        var session = await app.CreateSession(browser);
        var stored = await app.Services.GetRequiredService<IShareItPersistence>().FindAsync(session.Id);
        Assert.NotEqual(session.Pin, stored!.PinHash);
        Assert.True(app.Services.GetRequiredService<IPinHasher>().Verify(session.Pin, stored.PinHash));
    }

    [Fact]
    public async Task Hourly_creation_limit_reports_its_actual_retry_window()
    {
        await using var app = new AppFactory(); using var browser = app.Browser();
        for (var i = 0; i < 10; i++) await app.CreateSession(browser);
        await AppFactory.Csrf(browser);
        var blocked = await browser.PostAsJsonAsync("/api/v1/sessions", new { minutes = 60 });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.InRange(blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 3500, 3600);
    }

    [Fact]
    public async Task Session_wide_pin_lockout_reports_remaining_time_not_source_window()
    {
        var clock = new MutableClock();
        await using var app = new AppFactory(clock); using var browser = app.Browser(); using var terminal = app.Browser();
        var session = await app.CreateSession(browser);
        var wrong = session.Pin == "0000" ? "0001" : "0000";
        var guard = app.Services.GetRequiredService<ShareIt.Web.Authentication.CredentialGuard>();
        // Five failures from each of four sources exhaust the code-wide budget.
        for (var source = 0; source < 4; source++)
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(401, (await Assert.ThrowsAsync<ShareItException>(() => guard.VerifyAsync(session.Code, wrong, source.ToString(), CancellationToken.None))).Status);
        clock.Now = clock.Now.AddMinutes(10);
        Basic(terminal, session.Code, session.Pin);
        var blocked = await terminal.GetAsync($"/api/v1/sessions/{session.Code}");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(50), blocked.Headers.RetryAfter!.Delta);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
