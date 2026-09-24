using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using ShareIt.Core.DTOs;

namespace ShareIt.Web.Tests;

public class HostingTests
{
    private const string PublicHost = "share-it.sherif.online";
    private const string LanHost = "172.16.16.106";
    private const string ProxyIp = "192.0.2.10";

    private static WebApplicationFactory<Program> Configure(AppFactory factory, bool allowHttp = true) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AllowedHosts", $"{LanHost};{PublicHost}");
            builder.UseSetting("ShareIt:TrustedProxy", ProxyIp);
            builder.UseSetting("ShareIt:AllowHttp", allowHttp.ToString());
            builder.UseSetting("https_port", "443");
        });

    [Theory]
    [InlineData(LanHost, 200)]
    [InlineData(PublicHost, 200)]
    [InlineData("unrecognized.example", 400)]
    public async Task Deployment_accepts_both_hosts_and_rejects_other_hosts(string host, int status)
    {
        await using var factory = new AppFactory();
        await using var app = Configure(factory);
        using var client = app.CreateClient(new() { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
        Assert.Equal(status, (int)(await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData(ProxyIp, "https://share-it.sherif.online", 200, "https")]
    [InlineData("192.0.2.11", "https://share-it.sherif.online", 403, "http")]
    [InlineData(ProxyIp, "https://unrecognized.example", 403, "https")]
    public async Task Blazor_accepts_public_https_only_from_the_trusted_proxy_and_same_origin(
        string remoteIp, string origin, int status, string scheme)
    {
        await using var factory = new AppFactory();
        await using var app = Configure(factory);
        var context = await app.Server.SendAsync(ctx =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString(PublicHost);
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/_blazor/negotiate";
            ctx.Request.QueryString = new QueryString("?negotiateVersion=1");
            ctx.Request.Headers.Origin = origin;
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["X-Forwarded-For"] = "198.51.100.20";
        });
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(scheme, context.Request.Scheme);
        Assert.Equal(remoteIp == ProxyIp ? "198.51.100.20" : remoteIp, context.Connection.RemoteIpAddress?.ToString());
    }

    [Theory]
    [InlineData("http://172.16.16.106:8083", false)]
    [InlineData("https://share-it.sherif.online", true)]
    public async Task Sessions_work_on_both_origins_with_secure_cookies_for_https(string origin, bool secure)
    {
        await using var factory = new AppFactory();
        await using var app = Configure(factory);
        using var client = app.CreateClient(new() { BaseAddress = new Uri(origin), AllowAutoRedirect = false });
        await AppFactory.Csrf(client);
        using var response = await client.PostAsJsonAsync("/api/v1/sessions", new { minutes = 60 });
        response.EnsureSuccessStatusCode();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("shareit.browser="));
        Assert.Equal(secure, cookie.Split(';').Any(part => part.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase)));
        var session = (await response.Content.ReadFromJsonAsync<CreatedSession>())!;
        (await client.GetAsync($"/api/v1/sessions/{session.Code}")).EnsureSuccessStatusCode();
        Assert.Equal(secure, response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Production_still_redirects_http_when_lan_http_is_not_enabled()
    {
        await using var factory = new AppFactory();
        await using var app = Configure(factory, allowHttp: false);
        using var client = app.CreateClient(new() { BaseAddress = new Uri($"http://{PublicHost}"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal($"https://{PublicHost}/health", response.Headers.Location?.AbsoluteUri);
    }
}
