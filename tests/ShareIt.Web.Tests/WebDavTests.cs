using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using ShareIt.Core.Configuration;
using ShareIt.Core.Contracts;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;
using ShareIt.Core.Services;

namespace ShareIt.Web.Tests;

public sealed class WebDavTests
{
    private static readonly XNamespace Dav = "DAV:";
    private const string ExactText = "  printf 'hello'\r\n\t# مرحبا 😀\n";
    private static readonly byte[] FileBytes = [0, 1, 255, 42, 10];

    private static async Task<CreatedSession> Seed(AppFactory app, HttpClient browser)
    {
        var session = await app.CreateSession(browser);
        var caller = await app.Owner(session.Id);
        await app.Services.GetRequiredService<TextCardService>().SaveAsync(session.Id, caller, null, null, "deployment", ExactText, "bash");
        await app.Services.GetRequiredService<FileService>().UploadAsync(session.Id, caller, "config.bin", FileBytes.Length, new MemoryStream(FileBytes));
        return session;
    }

    private static void Basic(HttpClient client, CreatedSession session, string? pin = null) =>
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(session.Code + ":" + (pin ?? session.Pin))));

    private static Task<HttpResponseMessage> Propfind(HttpClient client, string path, string? depth = "1", string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
        if (depth != null) request.Headers.Add("Depth", depth);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        return client.SendAsync(request);
    }

    private static async Task<XDocument> Xml(HttpResponseMessage response, int status = 207)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Basic_is_required_for_all_read_verbs_even_with_a_browser_cookie()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser);
        foreach (var method in new[] { "OPTIONS", "PROPFIND", "GET", "HEAD" })
        {
            var response = await browser.SendAsync(new(new HttpMethod(method), $"/dav/{session.Code}/texts/1-deployment.sh"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, x => x.Scheme == "Basic");
            Assert.Null(response.Headers.Location);
            if (method == "HEAD") Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        Basic(client, session, session.Pin == "0000" ? "0001" : "0000");
        var invalid = await Propfind(client, $"/dav/{session.Code}/");
        await Xml(invalid, 401);
        Assert.Contains(invalid.Headers.WwwAuthenticate, x => x.Scheme == "Basic");
        Basic(client, session);
        await Xml(await Propfind(client, $"/dav/{session.Code}/"));
        var other = await app.CreateSession(browser);
        await Xml(await Propfind(client, $"/dav/{other.Code}/"), 403);
    }

    [Fact]
    public async Task Lists_root_collections_and_item_metadata_with_unknown_properties_separated()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session);
        var root = $"/dav/{session.Code}/";
        var xml = await Xml(await Propfind(client, root));
        Assert.Equal(new[] { root, root + "texts/", root + "files/" }, xml.Descendants(Dav + "href").Select(x => x.Value));
        Assert.Equal(3, xml.Descendants(Dav + "collection").Count());
        Assert.Single((await Xml(await Propfind(client, root, "0"))).Descendants(Dav + "response"));
        // A missing final slash is accepted without a credential-losing redirect.
        Assert.Equal(3, (await Xml(await Propfind(client, root.TrimEnd('/')))).Descendants(Dav + "response").Count());
        var request = "<propfind xmlns='DAV:' xmlns:x='urn:test'><prop><displayname/><getcontentlength/><getlastmodified/><getetag/><x:unknown/></prop></propfind>";
        xml = await Xml(await Propfind(client, root + "texts/1-deployment.sh", "0", request));
        Assert.Equal("1-deployment.sh", xml.Descendants(Dav + "displayname").Single().Value);
        Assert.Equal(Encoding.UTF8.GetByteCount(ExactText).ToString(), xml.Descendants(Dav + "getcontentlength").Single().Value);
        Assert.Contains("HTTP/1.1 404 Not Found", xml.Descendants(Dav + "status").Select(x => x.Value));
        Assert.Equal("HTTP/1.1 404 Not Found", xml.Descendants(XName.Get("unknown", "urn:test")).Single().Parent!.Parent!.Element(Dav + "status")!.Value);
        var get = await client.GetAsync(root + "texts/1-deployment.sh");
        Assert.Equal(get.Headers.ETag!.ToString(), xml.Descendants(Dav + "getetag").Single().Value);
        Assert.Equal(get.Content.Headers.LastModified!.Value.ToString("R"), xml.Descendants(Dav + "getlastmodified").Single().Value);
        xml = await Xml(await Propfind(client, root + "files/", "1"));
        Assert.Equal(new[] { root + "files/", root + "files/1-config.bin" }, xml.Descendants(Dav + "href").Select(x => x.Value));
        var names = await Xml(await Propfind(client, root + "texts/1-deployment.sh", "0", "<propfind xmlns='DAV:'><propname/></propfind>"));
        Assert.All(names.Descendants(Dav + "prop").Single().Elements(), x => Assert.Equal("", x.Value));
        var include = await Xml(await Propfind(client, root, "0", "<propfind xmlns='DAV:'><allprop/><include><missing/></include></propfind>"));
        Assert.NotEmpty(include.Descendants(Dav + "displayname")); Assert.NotEmpty(include.Descendants(Dav + "missing"));
    }

    [Fact]
    public async Task Property_requests_are_bounded_and_do_not_process_external_entities()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session); var root = $"/dav/{session.Code}/";
        foreach (var depth in new string?[] { null, "infinity" })
        {
            var xml = await Xml(await Propfind(client, root, depth), 403);
            Assert.NotEmpty(xml.Descendants(Dav + "propfind-finite-depth"));
        }
        await Xml(await Propfind(client, root, "2"), 400);
        foreach (var body in new[] { "<", "<wrong/>", "<propfind xmlns='DAV:'><prop/><allprop/></propfind>",
            "<!DOCTYPE p [<!ENTITY x SYSTEM 'file:///not-readable'>]><propfind xmlns='DAV:'><prop>&x;</prop></propfind>" })
            await Xml(await Propfind(client, root, "0", body), 400);
        var oversized = "<propfind xmlns='DAV:'><allprop/>" + new string(' ', 65536) + "</propfind>";
        await Xml(await Propfind(client, root, "0", oversized), 413);
        // StreamContent exercises the bound even when Content-Length is absent.
        using var request = new HttpRequestMessage(new("PROPFIND"), root);
        request.Headers.Add("Depth", "0"); request.Headers.TransferEncodingChunked = true;
        request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversized)));
        await Xml(await client.SendAsync(request), 413);
    }

    [Theory]
    [InlineData("texts/1-deployment.sh", true)]
    [InlineData("files/1-config.bin", false)]
    public async Task Reads_preserve_bytes_and_support_head_ranges_and_preconditions(string relative, bool text)
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session); var url = $"/dav/{session.Code}/{relative}";
        var bytes = text ? Encoding.UTF8.GetBytes(ExactText) : FileBytes;
        var get = await client.GetAsync(url); get.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes.Length, get.Content.Headers.ContentLength);
        Assert.Contains("bytes", get.Headers.AcceptRanges);
        var head = await client.SendAsync(new(HttpMethod.Head, url)); head.EnsureSuccessStatusCode();
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(get.Headers.ETag, head.Headers.ETag);
        foreach (var (range, expected) in new[] { ("bytes=1-3", bytes[1..4]), ("bytes=2-", bytes[2..]), ("bytes=-2", bytes[^2..]) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Add("Range", range);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(bytes.Length, response.Content.Headers.ContentRange!.Length);
        }
        using var outside = new HttpRequestMessage(HttpMethod.Get, url); outside.Headers.Add("Range", "bytes=99999-");
        var unsatisfiable = await client.SendAsync(outside);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal(bytes.Length, unsatisfiable.Content.Headers.ContentRange!.Length);
        using var cached = new HttpRequestMessage(HttpMethod.Get, url); cached.Headers.IfNoneMatch.Add(get.Headers.ETag!);
        var unchanged = await client.SendAsync(cached); Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        using var stale = new HttpRequestMessage(HttpMethod.Get, url); stale.Headers.IfMatch.Add(new("\"stale\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        using var changedRange = new HttpRequestMessage(HttpMethod.Get, url);
        changedRange.Headers.Range = new RangeHeaderValue(1, 3); changedRange.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
        var full = await client.SendAsync(changedRange); Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Browser_changes_update_validators_names_and_visibility()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session); var root = $"/dav/{session.Code}/";
        var caller = await app.Owner(session.Id); var service = app.Services.GetRequiredService<SessionService>();
        var texts = app.Services.GetRequiredService<TextCardService>(); var files = app.Services.GetRequiredService<FileService>();
        var previous = await client.GetAsync(root + "texts/1-deployment.sh");
        var snapshot = await service.GetAsync(session.Code, caller);
        await texts.SaveAsync(session.Id, caller, 1, snapshot.Texts[0].Version, "deployment", "updated", "bash");
        var changed = await client.GetAsync(root + "texts/1-deployment.sh");
        Assert.NotEqual(previous.Headers.ETag, changed.Headers.ETag);
        Assert.Equal("updated", await changed.Content.ReadAsStringAsync());
        snapshot = await service.GetAsync(session.Code, caller);
        await texts.SaveAsync(session.Id, caller, 1, snapshot.Texts[0].Version, "settings", "{}", "json");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "texts/1-deployment.sh")).StatusCode);
        Assert.Equal("{}", await client.GetStringAsync(root + "texts/1-settings.json"));
        await files.DeleteAsync(session.Id, caller, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "files/1-config.bin")).StatusCode);
        Assert.Single((await Xml(await Propfind(client, root + "files/"))).Descendants(Dav + "response"));
        await app.Services.GetRequiredService<IShareItPersistence>().MutateAsync(session.Id, (s, _) =>
        {
            s.Files.Add(new FileEntry { SessionId = s.Id, Number = 2, Name = "pending.bin", State = FileState.Pending });
            return true;
        });
        Assert.Single((await Xml(await Propfind(client, root + "files/"))).Descendants(Dav + "response"));
    }

    [Fact]
    public async Task Portable_numbered_names_handle_unicode_duplicates_and_literal_url_characters()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await app.CreateSession(browser); Basic(client, session); var caller = await app.Owner(session.Id);
        var texts = app.Services.GetRequiredService<TextCardService>();
        foreach (var title in new[] { "CON?*<>|. ", "CON?*<>|. ", "مرحبا & # %2F %25 café", "مرحبا & # %2F %25 cafe\u0301", "\uFFFEbad" })
            await texts.SaveAsync(session.Id, caller, null, null, title, title, "text");
        var files = app.Services.GetRequiredService<FileService>();
        await files.UploadAsync(session.Id, caller, new string('界', 170) + ".json", 0, new MemoryStream());
        var root = $"/dav/{session.Code}/";
        var xml = await Xml(await Propfind(client, root + "texts/"));
        var names = xml.Descendants(Dav + "displayname").Select(x => x.Value).Skip(1).ToArray();
        Assert.Equal("1-CON_____.txt", names[0]); Assert.Equal("2-CON_____.txt", names[1]);
        Assert.Equal(5, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("5-_bad.txt", names[4]);
        foreach (var href in xml.Descendants(Dav + "href").Skip(1))
        {
            Assert.DoesNotContain(" ", href.Value); Assert.DoesNotContain("#", href.Value);
            var read = await client.GetAsync(href.Value);
            Assert.True(read.IsSuccessStatusCode, $"{href.Value}: {read.StatusCode} {await read.Content.ReadAsStringAsync()}");
        }
        xml = await Xml(await Propfind(client, root + "files/"));
        var fileName = xml.Descendants(Dav + "displayname").Last().Value;
        Assert.InRange(Encoding.UTF8.GetByteCount(fileName), 1, 240); Assert.EndsWith(".json", fileName);
        var empty = await client.GetAsync(xml.Descendants(Dav + "href").Last().Value);
        empty.EnsureSuccessStatusCode(); Assert.Empty(await empty.Content.ReadAsByteArrayAsync());
        foreach (var path in new[] { "texts/1-anything.txt", "texts/1-CON_____.txt/", "files/%2Fetc", "files/%5Csecret", "texts/../unknown", "texts/%252e%252e", "texts//1-CON_____.txt" })
            Assert.False((await client.GetAsync(root + path)).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("PUT")][InlineData("DELETE")][InlineData("MKCOL")][InlineData("COPY")]
    [InlineData("MOVE")][InlineData("PROPPATCH")][InlineData("POST")][InlineData("PATCH")]
    public async Task Writes_are_denied_on_server_without_changing_content(string method)
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session); var root = $"/dav/{session.Code}/";
        var persistence = app.Services.GetRequiredService<IShareItPersistence>();
        var before = (await persistence.FindAsync(session.Id))!;
        foreach (var target in new[] { "texts/1-deployment.sh", "files/new.bin" })
        {
            using var request = new HttpRequestMessage(new(method), root + target) { Content = new StringContent("must not be written") };
            request.Headers.Add("Destination", "https://localhost" + root + "files/copied.bin");
            await Xml(await client.SendAsync(request), 403);
        }
        var after = (await persistence.FindAsync(session.Id))!;
        Assert.Equal(before.Revision, after.Revision); Assert.Equal(before.Texts.Count, after.Texts.Count); Assert.Equal(before.Files.Count, after.Files.Count);
        Assert.Equal(ExactText, await client.GetStringAsync(root + "texts/1-deployment.sh"));
        Assert.Equal(FileBytes, await client.GetByteArrayAsync(root + "files/1-config.bin"));
    }

    [Fact]
    public async Task Options_advertises_reads_without_locks_and_unsupported_methods_have_allow()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session); var root = $"/dav/{session.Code}/";
        foreach (var (path, expected) in new[] { (root, new[] { "OPTIONS", "PROPFIND" }), (root + "files/1-config.bin", new[] { "OPTIONS", "PROPFIND", "GET", "HEAD" }) })
        {
            var options = await client.SendAsync(new(HttpMethod.Options, path)); options.EnsureSuccessStatusCode();
            Assert.Equal(expected, options.Content.Headers.Allow); Assert.Equal(new[] { "1" }, options.Headers.GetValues("DAV"));
            foreach (var method in new[] { "LOCK", "UNLOCK", "REPORT" })
            {
                var response = await client.SendAsync(new(new(method), path));
                await Xml(response, 405); Assert.Equal(expected, response.Content.Headers.Allow);
            }
        }
        await Xml(await client.GetAsync(root), 405);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Expiry_and_closure_block_new_reads_and_property_requests(bool close)
    {
        var clock = new MutableClock();
        await using var app = new AppFactory(clock); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser); Basic(client, session);
        if (close) await app.Services.GetRequiredService<SessionService>().EndAsync(session.Id, await app.Owner(session.Id), false);
        else clock.Now = clock.Now.AddHours(2);
        foreach (var method in new[] { "GET", "HEAD", "OPTIONS", "PROPFIND" })
        {
            var response = await client.SendAsync(new(new(method), $"/dav/{session.Code}/files/1-config.bin"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, x => x.Scheme == "Basic");
        }
    }

    [Fact]
    public async Task Dav_limits_are_separate_and_pin_lockout_still_applies()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser);
        app.Services.GetRequiredService<ShareItLimits>().WebDavRequestsPerMinute = 8;
        Basic(client, session, session.Pin == "0000" ? "0001" : "0000");
        for (var i = 0; i < 5; i++) await Xml(await Propfind(client, $"/dav/{session.Code}/"), 401);
        var locked = await Propfind(client, $"/dav/{session.Code}/"); await Xml(locked, 429);
        Assert.NotNull(locked.Headers.RetryAfter);
        await client.GetAsync("/dav/missing/"); await client.GetAsync("/dav/missing/");
        var limited = await client.GetAsync("/dav/missing/"); await Xml(limited, 429); Assert.NotNull(limited.Headers.RetryAfter);
        (await browser.GetAsync($"/api/v1/sessions/{session.Code}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Full_session_metadata_burst_fits_the_default_dav_limit()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await app.CreateSession(browser); Basic(client, session);
        await app.Services.GetRequiredService<IShareItPersistence>().MutateAsync(session.Id, (s, _) =>
        {
            for (var i = 1; i <= 100; i++) s.Texts.Add(new TextCard { SessionId = s.Id, Number = i, Title = "text", Content = "x", UpdatedAtUtc = DateTime.UtcNow });
            for (var i = 1; i <= 50; i++) s.Files.Add(new FileEntry { SessionId = s.Id, Number = i, Name = "file.bin", State = FileState.Ready, CreatedAtUtc = DateTime.UtcNow, Sha256 = new string('a', 64) });
            return true;
        });
        var root = $"/dav/{session.Code}/";
        foreach (var collection in new[] { "texts/", "files/" })
        {
            var listing = await Xml(await Propfind(client, root + collection));
            foreach (var href in listing.Descendants(Dav + "href").Skip(1))
                await Xml(await Propfind(client, href.Value, "0"));
        }
        (await browser.GetAsync($"/api/v1/sessions/{session.Code}")).EnsureSuccessStatusCode();
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
