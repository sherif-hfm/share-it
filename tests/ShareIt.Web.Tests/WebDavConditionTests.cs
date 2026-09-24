using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;

namespace ShareIt.Web.Tests;

public sealed class WebDavConditionTests
{
    private static async Task<CreatedSession> Seed(AppFactory app, HttpClient browser, HttpClient client)
    {
        var session = await app.CreateSession(browser);
        var owner = await app.Owner(session.Id);
        await app.Services.GetRequiredService<TextCardService>().SaveAsync(session.Id, owner, null, null, "note", "original", "text");
        await app.Services.GetRequiredService<FileService>().UploadAsync(session.Id, owner, "data.bin", 3, new MemoryStream([1, 2, 3]));
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(session.Code + ":" + session.Pin)));
        return session;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string method, string condition)
    {
        var request = new HttpRequestMessage(new(method), path);
        request.Headers.TryAddWithoutValidation("If", condition);
        if (method == "PROPFIND") request.Headers.Add("Depth", "0");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Propfind_ignores_extensions_without_treating_nested_elements_as_selectors_or_properties()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client);
        var url = $"/dav/{session.Code}/texts/1-note.txt";
        async Task<XDocument> Properties(string body)
        {
            using var request = new HttpRequestMessage(new("PROPFIND"), url) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            request.Headers.Add("Depth", "0");
            using var response = await client.SendAsync(request);
            Assert.Equal(207, (int)response.StatusCode);
            return XDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        foreach (var selector in new[] { "allprop", "propname" })
        {
            var baseline = await Properties($"<propfind xmlns='DAV:'><{selector}/></propfind>");
            var extended = await Properties($"<propfind xmlns='DAV:' xmlns:x='urn:extension' x:flag='yes'><x:wrapper><prop/><include><getetag/></include></x:wrapper><{selector}><x:extension/><prop/></{selector}><x:extra/></propfind>");
            Assert.True(XNode.DeepEquals(baseline, extended));
        }
        var named = await Properties("<propfind xmlns='DAV:' xmlns:x='urn:extension'><x:wrapper><allprop/></x:wrapper><prop><getetag/><x:unknown><displayname/></x:unknown></prop></propfind>");
        XNamespace dav = "DAV:";
        Assert.Single(named.Descendants(dav + "getetag"));
        Assert.Empty(named.Descendants(dav + "displayname"));
        Assert.Equal("HTTP/1.1 404 Not Found", named.Descendants(XName.Get("unknown", "urn:extension")).Single().Parent!.Parent!.Element(dav + "status")!.Value);
    }

    [Theory]
    [InlineData("GET")][InlineData("HEAD")][InlineData("PROPFIND")][InlineData("OPTIONS")]
    public async Task If_evaluates_etags_negation_and_or_and_unavailable_lock_tokens(string method)
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client);
        var url = $"/dav/{session.Code}/texts/1-note.txt";
        using var original = await client.GetAsync(url);
        var current = original.Headers.ETag!.ToString();
        foreach (var (condition, matches) in new (string, bool)[]
        {
            ($"([{current}])", true), ("([\"stale\"])", false), ($"(Not [{current}])", false),
            ("(Not [\"stale\"])", true), ($"([\"stale\"]) ([{current}])", true),
            ($"([{current}] [\"stale\"])", false), ($"([{current}] Not [\"stale\"])", true),
            ($"([W/{current}])", false), ("(<DAV:no-lock>)", false),
            ("(Not <DAV:no-lock>)", true), ($"([{current}] <urn:uuid:missing>)", false),
            ($"([{current}] Not <urn:uuid:missing>)", true), ("([\"a]b\"])", false)
        })
        {
            using var response = await Send(client, url, method, condition);
            Assert.Equal(matches ? (method == "PROPFIND" ? 207 : 200) : 412, (int)response.StatusCode);
            if (method == "HEAD") Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            else if (!matches)
            {
                Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("precondition_failed", await response.Content.ReadAsStringAsync());
            }
            else if (method == "GET") Assert.Equal("original", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Tagged_conditions_resolve_only_the_authorized_session_and_support_alternative_resources()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client);
        var root = $"/dav/{session.Code}/";
        var text = root + "texts/1-note.txt";
        var file = root + "files/1-data.bin";
        using var textResponse = await client.GetAsync(text); var textTag = textResponse.Headers.ETag!.ToString();
        using var fileResponse = await client.GetAsync(file); var fileTag = fileResponse.Headers.ETag!.ToString();
        var other = await app.CreateSession(browser);
        await app.Services.GetRequiredService<FileService>().UploadAsync(other.Id, await app.Owner(other.Id), "data.bin", 3, new MemoryStream([1, 2, 3]));
        foreach (var (condition, matches) in new (string, bool)[]
        {
            ($"<{text}> ([{textTag}])", true), ($"<https://LOCALHOST:443{text}> ([{textTag}])", true),
            ($"<{file}> ([{fileTag}])", true), ($"<{file}> ([{textTag}])", false),
            ($"<{text}> ([\"stale\"]) ([{textTag}])", true),
            ($"<{text}> ([\"stale\"]) <{file}> ([{fileTag}])", true),
            ($"<{text}> ([\"stale\"]) <{file}> ([\"stale\"])", false),
            ($"<{root}missing> ([{fileTag}])", false), ($"<{root}missing> (Not [{fileTag}])", true),
            ($"<{root}> ([{fileTag}])", false), ($"<{text}/> ([{textTag}])", false),
            ($"<https://external.invalid{file}> ([{fileTag}])", false),
            ($"<http://localhost{file}> ([{fileTag}])", false),
            ($"</dav/{other.Code}/files/1-data.bin> ([{fileTag}])", false)
        })
        {
            using var response = await Send(client, text, "GET", condition);
            Assert.Equal(matches ? 200 : 412, (int)response.StatusCode);
        }
        // A collection PROPFIND can be conditional on a child's ETag.
        using var collection = await Send(client, root, "PROPFIND", $"<{file}> ([{fileTag}])");
        Assert.Equal(207, (int)collection.StatusCode);
        using var binary = await Send(client, file, "GET", $"([{fileTag}])");
        Assert.Equal(new byte[] { 1, 2, 3 }, await binary.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Tagged_percent_names_decode_once_and_browser_changes_invalidate_old_conditions()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client); var owner = await app.Owner(session.Id);
        var texts = app.Services.GetRequiredService<TextCardService>();
        const string title = "مرحبا %2F café";
        await texts.SaveAsync(session.Id, owner, null, null, title, "before", "text");
        var url = $"/dav/{session.Code}/texts/" + Uri.EscapeDataString("2-" + title + ".txt");
        using var original = await client.GetAsync(url); var etag = original.Headers.ETag!.ToString();
        using var matched = await Send(client, url, "GET", $"<{url}> ([{etag}])");
        Assert.Equal("before", await matched.Content.ReadAsStringAsync());
        var snapshot = await app.Services.GetRequiredService<SessionService>().GetAsync(session.Code, owner);
        await texts.SaveAsync(session.Id, owner, 2, snapshot.Texts.Single(x => x.Number == 2).Version, title, "after with a different size", "text");
        using var stale = await Send(client, url, "GET", $"<{url}> ([{etag}])");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var updated = await Send(client, url, "GET", $"<{url}> (Not [{etag}])");
        Assert.Equal("after with a different size", await updated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Malformed_and_oversized_conditions_fail_instead_of_being_ignored()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client); var url = $"/dav/{session.Code}/texts/1-note.txt";
        foreach (var condition in new[] { "()", "([])", "([*])", "([\"stale\"]", "(Not)", "(<relative>)", "<relative> ([\"x\"])",
            $"<{url}>", $"<{url}> ()", "(Not <DAV:no-lock>) garbage", $"(Not <DAV:no-lock>) <{url}> ([\"x\"])",
            "(Not <DAV:no-lock>) ([\"bad\tvalue\"])", "(<urn:invalid token>)", "<//localhost/path> ([\"x\"])",
            $"<{url}#fragment> ([\"x\"])", "(<urn:bad\\token>)" })
        {
            using var response = await Send(client, url, "GET", condition);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{condition}: {(int)response.StatusCode}");
        }
        using var oversized = await Send(client, url, "GET", "([\"" + new string('x', 8192) + "\"])");
        Assert.Equal(431, (int)oversized.StatusCode);
        using var multiple = new HttpRequestMessage(HttpMethod.Get, url);
        multiple.Headers.TryAddWithoutValidation("If", new[] { "(Not <DAV:no-lock>)", "(Not <DAV:no-lock>)" });
        using var duplicate = await client.SendAsync(multiple);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        // HttpClient drops empty header values; inject them at the server boundary.
        foreach (var empty in new[] { "", " " })
        {
            var context = await app.Server.SendAsync(ctx =>
            {
                ctx.Request.Scheme = "https"; ctx.Request.Host = new("localhost");
                ctx.Request.Method = "GET"; ctx.Request.Path = url;
                ctx.Request.Headers.Authorization = client.DefaultRequestHeaders.Authorization!.ToString();
                ctx.Request.Headers["If"] = empty;
            });
            Assert.Equal(400, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Authentication_and_read_only_policy_still_take_precedence_over_if_conditions()
    {
        await using var app = new AppFactory(); using var browser = app.Browser(); using var client = app.Browser();
        var session = await Seed(app, browser, client); var url = $"/dav/{session.Code}/texts/1-note.txt";
        using var unauthenticated = await Send(browser, url, "GET", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var other = await app.CreateSession(browser);
        using var denied = await Send(client, $"/dav/{other.Code}/", "PROPFIND", "invalid");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var write = await Send(client, url, "PUT", "(Not <DAV:no-lock>)");
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        using var locked = await Send(client, url, "LOCK", "invalid");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, locked.StatusCode);
        using var ordinary = new HttpRequestMessage(HttpMethod.Get, url);
        ordinary.Headers.TryAddWithoutValidation("If", "(Not <DAV:no-lock>)"); ordinary.Headers.IfMatch.Add(new("\"stale\""));
        using var standard = await client.SendAsync(ordinary);
        Assert.Equal(HttpStatusCode.PreconditionFailed, standard.StatusCode);
    }
}
