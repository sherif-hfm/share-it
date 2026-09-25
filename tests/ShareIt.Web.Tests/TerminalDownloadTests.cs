using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;

namespace ShareIt.Web.Tests;

public class TerminalDownloadTests
{
    private static void Basic(HttpClient client, CreatedSession session, string? pin = null, string? code = null) =>
        client.DefaultRequestHeaders.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes((code ?? session.Code) + ":" + (pin ?? session.Pin))));

    private sealed record Content(CreatedSession Session, string Text, byte[] Bytes, string FileName);

    private static async Task<Content> Seed(AppFactory app, HttpClient browser, string marker = "one")
    {
        var session = await app.CreateSession(browser);
        var owner = await app.Owner(session.Id);
        var text = $"  {marker}\r\n\t# مرحبا 😀\n";
        var bytes = Encoding.UTF8.GetBytes(marker).Concat(new byte[] { 0, 255, 13, 10 }).ToArray();
        var fileName = $"تقرير {marker}.bin";
        await app.Services.GetRequiredService<TextCardService>().SaveAsync(session.Id, owner, null, null, "script", text, "bash");
        using var stream = new MemoryStream(bytes);
        await app.Services.GetRequiredService<FileService>().UploadAsync(session.Id, owner, fileName, bytes.Length, stream);
        return new(session, text, bytes, fileName);
    }

    [Theory]
    [InlineData("t")]
    [InlineData("f")]
    public async Task Short_routes_select_the_authenticated_session_and_match_legacy_downloads(string kind)
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var terminal = app.Browser();
        var first = await Seed(app, browser);
        var second = await Seed(app, browser, "two");
        foreach (var content in new[] { first, second })
        {
            // Normalization still accepts uppercase codes without the display hyphen.
            Basic(terminal, content.Session, code: content.Session.Code.Replace("-", "").ToUpperInvariant());
            using var response = await terminal.GetAsync($"/{kind}/1");
            response.EnsureSuccessStatusCode();
            Assert.Equal(kind == "t" ? Encoding.UTF8.GetBytes(content.Text) : content.Bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(kind == "t" ? "text/plain" : "application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            if (kind == "t") Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
            else
            {
                Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
                Assert.Equal(content.FileName, response.Content.Headers.ContentDisposition.FileNameStar);
            }
            var legacy = $"/api/v1/sessions/{content.Session.Code}/" + (kind == "t" ? "texts/1/raw" : "files/1");
            using var existing = await terminal.GetAsync(legacy);
            existing.EnsureSuccessStatusCode();
            Assert.Equal(await existing.Content.ReadAsByteArrayAsync(), await response.Content.ReadAsByteArrayAsync());
        }
        // Even a browser with grants for both sessions must use the Basic session.
        Basic(browser, first.Session);
        using var withCookie = await browser.GetAsync($"/{kind}/1");
        withCookie.EnsureSuccessStatusCode();
        Assert.Equal(kind == "t" ? Encoding.UTF8.GetBytes(first.Text) : first.Bytes, await withCookie.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/t/1")]
    [InlineData("/f/1")]
    public async Task Short_routes_require_basic_even_with_browser_cookies(string path)
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var anonymous = app.Browser();
        await Seed(app, browser);
        using var missing = await anonymous.GetAsync(path);
        AssertChallenge(missing);
        foreach (var authorization in new string?[] { null, "Bearer token", "Basic !!!", "Basic bm9jb2xvbg==" })
        {
            browser.DefaultRequestHeaders.Remove("Authorization");
            if (authorization != null) browser.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
            using var response = await browser.GetAsync(path);
            AssertChallenge(response);
        }
    }

    [Theory]
    [InlineData("/t/1", false)]
    [InlineData("/f/1", false)]
    [InlineData("/t/1", true)]
    [InlineData("/f/1", true)]
    public async Task Wrong_pins_and_ended_sessions_return_generic_challenges(string path, bool expire)
    {
        var clock = new MutableClock();
        await using var app = new AppFactory(clock);
        using var browser = app.Browser(); using var terminal = app.Browser();
        var content = await Seed(app, browser);
        var session = content.Session;
        Basic(terminal, session, session.Pin == "0000" ? "0001" : "0000");
        using var wrong = await terminal.GetAsync(path);
        AssertChallenge(wrong);
        Basic(terminal, session);
        using var valid = await terminal.GetAsync(path);
        valid.EnsureSuccessStatusCode();
        if (expire) clock.Now = clock.Now.AddHours(2);
        else await app.Services.GetRequiredService<SessionService>().EndAsync(session.Id, await app.Owner(session.Id), false);
        using var ended = await terminal.GetAsync(path);
        AssertChallenge(ended);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await ended.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Short_and_legacy_routes_share_the_pin_attempt_budget()
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var terminal = app.Browser();
        var session = await app.CreateSession(browser);
        var paths = new[] { "/t/1", "/f/1", $"/api/v1/sessions/{session.Code}" };
        Basic(terminal, session, session.Pin == "0000" ? "0001" : "0000");
        for (var i = 0; i < 5; i++)
        {
            using var response = await terminal.GetAsync(paths[i % paths.Length]);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Basic(terminal, session);
        foreach (var path in paths)
        {
            using var response = await terminal.GetAsync(path);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        }
        using var browserRead = await browser.GetAsync(paths[2]);
        browserRead.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("t")]
    [InlineData("f")]
    public async Task Missing_and_deleted_items_are_unavailable(string kind)
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var terminal = app.Browser();
        var content = await Seed(app, browser);
        Basic(terminal, content.Session);
        foreach (var number in new[] { 0, -1, 2 })
        {
            using var missing = await terminal.GetAsync($"/{kind}/{number}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        var owner = await app.Owner(content.Session.Id);
        if (kind == "t")
        {
            var snapshot = await app.Services.GetRequiredService<SessionService>().GetAsync(content.Session.Id, owner);
            await app.Services.GetRequiredService<TextCardService>().DeleteAsync(content.Session.Id, owner, 1, snapshot.Texts.Single().Version);
        }
        else await app.Services.GetRequiredService<FileService>().DeleteAsync(content.Session.Id, owner, 1);
        using var deleted = await terminal.GetAsync($"/{kind}/1");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Theory]
    [InlineData("/t/1")]
    [InlineData("/f/1")]
    public async Task Short_routes_reject_writes(string path)
    {
        await using var app = new AppFactory();
        using var browser = app.Browser(); using var terminal = app.Browser();
        var content = await Seed(app, browser);
        Basic(terminal, content.Session);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, path) { Content = new StringContent("changed") };
            using var response = await terminal.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        using var read = await terminal.GetAsync(path);
        read.EnsureSuccessStatusCode();
        Assert.Equal(path.StartsWith("/t") ? Encoding.UTF8.GetBytes(content.Text) : content.Bytes, await read.Content.ReadAsByteArrayAsync());
    }

    private static void AssertChallenge(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Basic");
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
