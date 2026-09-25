using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;

namespace ShareIt.Web.Tests.Browser;

[Collection("browser")]
public sealed class OfflineBrowserTests(BrowserFixture fixture)
{
    [Fact]
    public async Task Cold_browsers_share_text_and_files_with_all_external_requests_blocked()
    {
        var origin = new Uri(fixture.Url);
        var externalRequests = new ConcurrentQueue<string>();
        var localRequests = new ConcurrentQueue<string>();
        var localWebSockets = new ConcurrentQueue<string>();

        // Each context starts without cached assets, cookies, or service workers.
        // The controlled integration server may use a private test CA. Production
        // clients must still trust the LAN server's certificate normally.
        await using var first = await fixture.Browser.NewContextAsync(new()
        {
            ServiceWorkers = ServiceWorkerPolicy.Block,
            IgnoreHTTPSErrors = true,
            AcceptDownloads = true
        });
        await using var second = await fixture.Browser.NewContextAsync(new()
        {
            ServiceWorkers = ServiceWorkerPolicy.Block,
            IgnoreHTTPSErrors = true,
            AcceptDownloads = true
        });
        await RestrictToApp(first);
        await RestrictToApp(second);

        var owner = await first.NewPageAsync();
        await owner.GotoAsync(fixture.Url);
        await owner.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true }).ClickAsync();
        await owner.GetByRole(AriaRole.Dialog).WaitForAsync();
        var credentials = await owner.Locator(".credentials strong").AllTextContentsAsync();
        Assert.Equal(2, credentials.Count);
        var code = credentials[0];
        var pin = credentials[1];
        await owner.GetByRole(AriaRole.Button, new() { Name = "Open workspace" }).ClickAsync();
        await owner.Locator(".workspace").WaitForAsync();

        var guest = await second.NewPageAsync();
        await guest.GotoAsync(fixture.Url);
        await guest.GetByRole(AriaRole.Tab, new() { Name = "Join a session" }).ClickAsync();
        await guest.GetByLabel("SESSION CODE", new() { Exact = true }).FillAsync(code);
        await guest.GetByLabel("4-DIGIT PIN", new() { Exact = true }).FillAsync(pin);
        await guest.GetByRole(AriaRole.Button, new() { Name = "Join session", Exact = true }).ClickAsync();
        await guest.Locator(".workspace").WaitForAsync();

        const string content = "# Private network commands\nprintf 'LAN ready\\n'\n  keep whitespace\n";
        await owner.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await owner.GetByLabel("TITLE", new() { Exact = true }).FillAsync("LAN commands");
        await owner.GetByLabel("FORMAT", new() { Exact = true }).SelectOptionAsync("bash");
        await owner.Locator("#text-content").FillAsync(content);
        await owner.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await Assertions.Expect(guest.Locator(".card-content")).ToHaveTextAsync(content);

        // Use an independent cookie-free client, as curl does. Do not follow any
        // redirects: this test client may only request the known app endpoint.
        await using var terminal = await fixture.Playwright.APIRequest.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            MaxRedirects = 0,
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(code + ":" + pin))
            }
        });
        var raw = await terminal.GetAsync(new Uri(origin, "/t/1").AbsoluteUri);
        Assert.Equal(200, raw.Status);
        Assert.Equal(content, await raw.TextAsync());

        const string fileName = "lan-config.bin";
        byte[] bytes = [0, 1, 2, 10, 13, 127, 128, 254, 255];
        await owner.GetByLabel("Upload files", new() { Exact = true }).SetInputFilesAsync(new FilePayload
        {
            Name = fileName,
            MimeType = "application/octet-stream",
            Buffer = bytes
        });
        var downloadLink = guest.GetByRole(AriaRole.Link, new() { Name = "Download " + fileName, Exact = true });
        await Assertions.Expect(downloadLink).ToBeVisibleAsync();
        var download = await guest.RunAndWaitForDownloadAsync(() => downloadLink.ClickAsync());
        Assert.Equal(fileName, download.SuggestedFilename);
        await using (var downloaded = await download.CreateReadStreamAsync())
        {
            using var actual = new MemoryStream();
            await downloaded.CopyToAsync(actual);
            Assert.Equal(bytes, actual.ToArray());
        }
        Assert.Null(await download.FailureAsync());

        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SHAREIT_E2E_CA_FILE") is { Length: > 0 } caFile)
        {
            Assert.Equal("https", origin.Scheme);
            Assert.True(File.Exists(caFile), "SHAREIT_E2E_CA_FILE must point to the controlled server's CA certificate.");
            Assert.Equal(Encoding.UTF8.GetBytes(content), await CurlWithTrustedCa("/t/1"));
            Assert.Equal(bytes, await CurlWithTrustedCa("/f/1"));

            async Task<byte[]> CurlWithTrustedCa(string path)
            {
                var info = new ProcessStartInfo("curl.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // Disable user curlrc/proxy settings and retain normal certificate
                // and hostname validation. This private test CA publishes no CRL.
                foreach (var argument in new[]
                {
                    "--disable", "--fail", "--silent", "--show-error", "--max-time", "15",
                    "--cacert", caFile, "--ssl-revoke-best-effort", "--proto", "=https",
                    "--noproxy", "*", "--config", "-", new Uri(origin, path).AbsoluteUri
                }) info.ArgumentList.Add(argument);
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start curl.exe.");
                using var output = new MemoryStream();
                var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
                var readError = process.StandardError.ReadToEndAsync();
                // Synthetic credentials stay out of process arguments and logs.
                await process.StandardInput.WriteLineAsync($"user = \"{code}:{pin}\"");
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync();
                    throw;
                }
                await readOutput;
                var error = await readError;
                Assert.True(process.ExitCode == 0, $"curl failed with exit code {process.ExitCode}: {error}");
                return output.ToArray();
            }
        }

        foreach (var page in new[] { owner, guest })
        {
            Assert.True(await page.EvaluateAsync<bool>("""
                async () => {
                    const inter = await document.fonts.load('14px Inter');
                    const mono = await document.fonts.load('14px "JetBrains Mono"');
                    await document.fonts.ready;
                    return inter.length > 0 && mono.length > 0 &&
                        [...inter, ...mono].every(font => font.status === 'loaded');
                }
                """));
        }
        Assert.Contains(localRequests, url => new Uri(url).AbsolutePath == "/fonts/InterVariable.woff2");
        Assert.Contains(localRequests, url => new Uri(url).AbsolutePath == "/fonts/JetBrainsMono-Regular.woff2");
        Assert.NotEmpty(localWebSockets);

        await guest.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await guest.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(owner.Locator(".closed-state")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(owner.Locator(".text-card")).ToHaveCountAsync(0);
        await Assertions.Expect(owner.Locator(".file-row")).ToHaveCountAsync(0);
        await first.CloseAsync();
        await second.CloseAsync();
        Assert.Empty(externalRequests);

        bool IsAppUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            var scheme = uri.Scheme switch { "ws" => "http", "wss" => "https", var other => other };
            return scheme == origin.Scheme && uri.Port == origin.Port &&
                string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
        }

        async Task RestrictToApp(IBrowserContext context)
        {
            await context.RouteAsync("**/*", async route =>
            {
                if (IsAppUrl(route.Request.Url))
                {
                    localRequests.Enqueue(route.Request.Url);
                    await route.ContinueAsync();
                }
                else
                {
                    externalRequests.Enqueue(route.Request.Url);
                    await route.AbortAsync("internetdisconnected");
                }
            });
            // HTTP routing does not intercept WebSockets. Install this guard before
            // app scripts execute so only the app's binary Blazor socket can open.
            // Playwright .NET 1.62 WebSocketRoute cannot parse these binary frames.
            context.Page += (_, page) => page.WebSocket += (_, socket) =>
            {
                if (IsAppUrl(socket.Url)) localWebSockets.Enqueue(socket.Url);
            };
            await context.ExposeFunctionAsync("reportOfflineWebSocket", (string url) => externalRequests.Enqueue(url));
            await context.AddInitScriptAsync($$"""
                (() => {
                    const allowedOrigin = {{System.Text.Json.JsonSerializer.Serialize(origin.GetLeftPart(UriPartial.Authority))}};
                    window.WebSocket = new Proxy(window.WebSocket, {
                        construct(target, args, newTarget) {
                            const url = new URL(args[0], location.href);
                            if (url.protocol === 'ws:') url.protocol = 'http:';
                            if (url.protocol === 'wss:') url.protocol = 'https:';
                            if (url.origin !== allowedOrigin) {
                                window.reportOfflineWebSocket(String(args[0]));
                                throw new DOMException('External connection blocked by offline test.', 'SecurityError');
                            }
                            return Reflect.construct(target, args, newTarget);
                        }
                    });
                })();
                """);
        }
    }
}
