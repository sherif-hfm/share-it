using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;

namespace ShareIt.Web.Tests.Browser;

[CollectionDefinition("browser", DisableParallelization = true)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>;

public sealed class BrowserFixture : IAsyncLifetime
{
    private Process? server;
    private readonly StringBuilder output = new();
    public IPlaywright Playwright { get; private set; } = default!;
    public IBrowser Browser { get; private set; } = default!;
    public string Url { get; private set; } = "";
    public string Root { get; private set; } = "";
    public string Artifacts { get; private set; } = "";
    public async Task InitializeAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ShareIt.sln"))) directory = directory.Parent;
        Root = directory?.FullName ?? throw new InvalidOperationException("Run browser tests from a Share-It checkout.");
        Artifacts = Path.Combine(Root, ".artifacts", "screenshots"); Directory.CreateDirectory(Artifacts);
        Url = Environment.GetEnvironmentVariable("SHAREIT_E2E_URL") ?? "";
        if (Url == "")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Url = $"http://127.0.0.1:{port}";
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ShareIt.Web.dll"));
            info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(Path.Combine(Root, "src", "ShareIt.Web"));
            info.ArgumentList.Add("--urls"); info.ArgumentList.Add(Url);
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            info.Environment["ShareIt__DataPath"] = Path.Combine(Root, ".artifacts", "browser-data", Guid.NewGuid().ToString("N"));
            server = new Process { StartInfo = info };
            server.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            server.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using var client = new HttpClient();
            var ready = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (server.HasExited) throw new InvalidOperationException(output.ToString());
                try { if ((await client.GetAsync(Url + "/health")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            if (!ready) throw new InvalidOperationException("Test server failed to start. " + output);
        }
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = true });
    }
    public async Task DisposeAsync()
    {
        if (Browser != null) await Browser.DisposeAsync();
        Playwright?.Dispose();
        if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
        server?.Dispose();
    }
}

[Collection("browser")]
public class BrowserTests(BrowserFixture fixture)
{
    private async Task<IPage> Page(IBrowserContext context, string? origin = null)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(origin ?? fixture.Url);
        await page.Locator(".home").WaitForAsync();
        return page;
    }
    private async Task<(string Code, string Pin)> Create(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).WaitForAsync();
        var values = await page.Locator(".credentials strong").AllTextContentsAsync();
        Assert.Equal(2, values.Count);
        await page.GetByRole(AriaRole.Button, new() { Name = "Open workspace" }).ClickAsync();
        await page.Locator(".workspace").WaitForAsync();
        return (values[0], values[1]);
    }

    [Fact]
    public async Task Created_session_cancellation_failure_keeps_credentials_and_allows_retry()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await Page(context);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync();
        var credentials = await dialog.Locator(".credentials strong").AllTextContentsAsync();
        var cancellation = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/v1/sessions/*/cancel", route => { cancellation.TrySetResult(route); return Task.CompletedTask; });
        var close = dialog.GetByRole(AriaRole.Button, new() { Name = "Close dialog", Exact = true });
        await close.ClickAsync();
        var pending = await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assertions.Expect(close).ToBeDisabledAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Open workspace" })).ToBeDisabledAsync();
        await page.Keyboard.PressAsync("Escape");
        await pending.FulfillAsync(new() { Status = 503, ContentType = "application/json", Body = "{\"detail\":\"Please try again.\"}" });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("Could not cancel the session.");
        Assert.Equal(credentials, await dialog.Locator(".credentials strong").AllTextContentsAsync());
        await Assertions.Expect(close).ToBeEnabledAsync();
        await page.UnrouteAsync("**/api/v1/sessions/*/cancel");
        await close.ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".home")).ToBeVisibleAsync();
        await page.GotoAsync(fixture.Url + "/s/" + credentials[0]);
        await Assertions.Expect(page.Locator(".closed-state")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Start a new session" })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dismissing_created_session_cancels_it_without_opening_workspace(bool escape)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await Page(context);
        var homeUrl = page.Url;
        var create = page.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true });
        await create.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync();
        var credentials = await dialog.Locator(".credentials strong").AllTextContentsAsync();
        if (escape) await page.Keyboard.PressAsync("Escape");
        else await dialog.GetByRole(AriaRole.Button, new() { Name = "Close dialog", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        Assert.Equal(homeUrl, page.Url);
        await Assertions.Expect(create).ToBeFocusedAsync();

        await using var other = await fixture.Browser.NewContextAsync();
        var join = await Page(other);
        await join.GetByRole(AriaRole.Tab, new() { Name = "Join a session" }).ClickAsync();
        await join.GetByLabel("SESSION CODE", new() { Exact = true }).FillAsync(credentials[0]);
        await join.GetByLabel("4-DIGIT PIN", new() { Exact = true }).FillAsync(credentials[1]);
        await join.GetByRole(AriaRole.Button, new() { Name = "Join session", Exact = true }).ClickAsync();
        await Assertions.Expect(join.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Assertions.Expect(join.Locator(".workspace")).ToHaveCountAsync(0);
        var replacement = await Create(page);
        Assert.NotEqual(credentials[0], replacement.Code);
        await Assertions.Expect(page.Locator(".workspace")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Two_browsers_share_text_files_and_receive_session_closure()
    {
        await using var first = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        await using var second = await fixture.Browser.NewContextAsync();
        var page = await Page(first);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "home-dark.png"), FullPage = true });
        var session = await Create(page);
        var hasRandomUUID = await page.EvaluateAsync<bool>("""
            () => {
                window.uploadUuidCalls = 0;
                if (typeof crypto.randomUUID !== 'function') return false;
                const randomUUID = crypto.randomUUID.bind(crypto);
                crypto.randomUUID = () => { window.uploadUuidCalls++; return randomUUID(); };
                return true;
            }
            """);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "workspace-empty.png"), FullPage = true });
        var other = await Page(second);
        await other.GetByRole(AriaRole.Tab, new() { Name = "Join a session" }).ClickAsync();
        await other.GetByLabel("SESSION CODE", new() { Exact = true }).FillAsync(session.Code);
        await other.GetByLabel("4-DIGIT PIN", new() { Exact = true }).FillAsync(session.Pin);
        await other.GetByRole(AriaRole.Button, new() { Name = "Join session", Exact = true }).ClickAsync();
        await other.Locator(".workspace").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Deployment commands");
        await page.GetByLabel("FORMAT", new() { Exact = true }).SelectOptionAsync("bash");
        await page.Locator("#text-content").FillAsync("# Check service health\ncurl -fsS http://localhost:8080/health\n\n# Follow the logs\ndocker compose logs -f --tail 100");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await Assertions.Expect(other.GetByRole(AriaRole.Heading, new() { Name = "Deployment commands" })).ToBeVisibleAsync();
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "appsettings.json", MimeType = "application/json", Buffer = Encoding.UTF8.GetBytes("{\"environment\":\"staging\",\"port\":8080}\n") });
        await Assertions.Expect(other.Locator(".file-details strong")).ToHaveTextAsync("appsettings.json");
        if (hasRandomUUID) Assert.Equal(1, await page.EvaluateAsync<int>("window.uploadUuidCalls"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "workspace-populated.png"), FullPage = true });
        await other.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await other.GetByRole(AriaRole.Dialog).WaitForAsync();
        await other.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "end-session.png"), FullPage = true });
        await other.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".closed-state")).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.Empty(await page.Locator(".text-card").AllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uploads_without_randomUUID_keep_separate_progress_and_preserve_file_contents(bool blocksRandomUUID)
    {
        var serverUri = new Uri(fixture.Url);
        var insecureOrigin = serverUri.Scheme == "http" && serverUri.IsLoopback;
        var origin = insecureOrigin ? new UriBuilder(serverUri) { Host = "shareit.test" }.Uri.GetLeftPart(UriPartial.Authority) : fixture.Url;
        await using var browser = await fixture.Playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = insecureOrigin ? ["--host-resolver-rules=MAP shareit.test 127.0.0.1"] : []
        });
        await using var context = await browser.NewContextAsync();
        // Use real insecure HTTP locally; simulate the missing API against an external HTTPS fixture.
        if (!insecureOrigin)
            await context.AddInitScriptAsync("Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: undefined });");
        var page = await Page(context, origin);
        if (insecureOrigin) Assert.False(await page.EvaluateAsync<bool>("window.isSecureContext"));
        Assert.True(await page.EvaluateAsync<bool>("typeof crypto.randomUUID === 'undefined'"));
        await Create(page);
        if (blocksRandomUUID)
            await page.EvaluateAsync("Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: () => { throw new DOMException('Blocked for test', 'SecurityError'); } });");

        byte[][] contents = [[0, 1, 127, 128, 255], [255, 128, 127, 1, 0], [10, 20, 30]];
        await page.Locator("input[type=file]").SetInputFilesAsync(new[]
        {
            new FilePayload { Name = "duplicate.bin", MimeType = "application/octet-stream", Buffer = contents[0] },
            new FilePayload { Name = "duplicate.bin", MimeType = "application/octet-stream", Buffer = contents[1] }
        });
        var progress = page.Locator(".upload-progress > div > span:last-child");
        await Assertions.Expect(progress).ToHaveTextAsync(new[] { "complete", "complete" });
        await Assertions.Expect(page.Locator(".file-details strong")).ToHaveTextAsync(new[] { "duplicate.bin", "duplicate.bin" });

        // A later drag-and-drop batch must add a progress row instead of reusing an earlier ID.
        await page.WaitForFunctionAsync("document.querySelector('input[type=file]').value === ''");
        await page.Locator(".dropzone").EvaluateAsync("""
            (dropzone, bytes) => {
                const transfer = new DataTransfer();
                transfer.items.add(new File([new Uint8Array(bytes)], 'dropped.bin', { type: 'application/octet-stream' }));
                dropzone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
            }
            """, contents[2].Select(value => (int)value).ToArray());
        await Assertions.Expect(progress).ToHaveTextAsync(new[] { "complete", "complete", "complete" });
        var downloads = page.Locator(".file-row a[download]");
        await Assertions.Expect(downloads).ToHaveCountAsync(3);
        for (var index = 0; index < contents.Length; index++)
        {
            var href = await downloads.Nth(index).GetAttributeAsync("href");
            var downloaded = await page.EvaluateAsync<int[]>("""
                async url => {
                    const response = await fetch(url);
                    if (!response.ok) throw new Error(`Download failed: ${response.status}`);
                    return Array.from(new Uint8Array(await response.arrayBuffer()));
                }
                """, href);
            Assert.Equal(contents[index].Select(value => (int)value).ToArray(), downloaded);
        }
    }

    [Fact]
    public async Task Themes_and_small_viewports_do_not_overflow()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1024, Height = 768 } });
        var page = await Page(context);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Change color theme" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "home-light-rdp.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "home-mobile.png"), FullPage = true });
        await Create(page);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "workspace-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(512, 384);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
    }

    [Fact]
    public async Task Conflict_copies_current_draft_and_clipboard_fallback_preserves_editor()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { Permissions = ["clipboard-read", "clipboard-write"] });
        var page = await Page(context); var session = await Create(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Shared note");
        await page.Locator("#text-content").FillAsync("original");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        var other = await context.NewPageAsync(); await other.GotoAsync(fixture.Url + "/s/" + session.Code);
        await other.Locator(".workspace").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Shared note", Exact = true }).ClickAsync();
        await page.Locator("#text-content").FillAsync("my unsaved draft");
        await other.GetByRole(AriaRole.Button, new() { Name = "Edit Shared note", Exact = true }).ClickAsync();
        await other.Locator("#text-content").FillAsync("saved on other machine");
        await other.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await page.BringToFrontAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".conflict-actions")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#text-content")).ToHaveValueAsync("my unsaved draft");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "edit-conflict.png"), FullPage = true });
        const string latestDraft = "newest edited draft after conflict\n  keep whitespace";
        await page.EvaluateAsync("window.lastClipboardWrite = null; const write = navigator.clipboard.writeText.bind(navigator.clipboard); navigator.clipboard.writeText = text => { window.lastClipboardWrite = text; return write(text); }");
        await page.Locator("#text-content").FillAsync(latestDraft);
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy my draft", Exact = true }).ClickAsync();
        Assert.Equal(latestDraft, await page.EvaluateAsync<string>("window.lastClipboardWrite"));
        // Windows normalizes native clipboard line endings to CRLF.
        Assert.Equal(latestDraft, (await page.EvaluateAsync<string>("navigator.clipboard.readText()")).Replace("\r\n", "\n"));
        await page.EvaluateAsync("() => { navigator.clipboard.writeText = () => Promise.reject(new Error('Permission denied for test')); }");
        const string fallbackDraft = latestDraft + "\nCopied through compatibility fallback";
        await page.Locator("#text-content").FillAsync(fallbackDraft);
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy my draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Status)).ToHaveTextAsync("Copied to clipboard.");
        Assert.Equal(fallbackDraft, (await page.EvaluateAsync<string>("navigator.clipboard.readText()")).Replace("\r\n", "\n"));
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("#text-content")).ToBeFocusedAsync();
        await page.EvaluateAsync("() => { document.execCommand = () => false; }");
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy my draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveValueAsync(fallbackDraft);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#text-content")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator("#text-content")).ToHaveValueAsync(fallbackDraft);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save as a new card" }).ClickAsync();
        await Assertions.Expect(page.Locator(".text-card")).ToHaveCountAsync(2);
        var raw = await context.APIRequest.GetAsync(fixture.Url + $"/api/v1/sessions/{session.Code}/texts/2/raw");
        Assert.Equal(fallbackDraft, await raw.TextAsync());
    }

    [Fact]
    public async Task Credentials_and_curl_copy_show_feedback_inside_the_active_dialog()
    {
        await using var context = await fixture.Browser.NewContextAsync(new() { Permissions = ["clipboard-read", "clipboard-write"] });
        var page = await Page(context);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync();
        var code = await dialog.Locator(".credentials strong").First.TextContentAsync();
        var pin = (await dialog.Locator(".credentials strong").Nth(1).TextContentAsync())!;
        await dialog.Locator(".credentials button").First.ClickAsync();
        Assert.Equal(code, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToHaveTextAsync("Copied to clipboard.");
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToHaveCSSAsync("opacity", "1");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "dialog-copy-feedback.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Open workspace" }).ClickAsync();
        await page.Locator(".workspace").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Terminal note");
        await page.Locator("#text-content").FillAsync("echo ready");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await page.Locator(".text-card").GetByRole(AriaRole.Button, new() { Name = "curl", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy command", Exact = true }).ClickAsync();
        Assert.Equal(await dialog.Locator(".curl-command").TextContentAsync(), await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToHaveTextAsync("Copied to clipboard.");
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToHaveCSSAsync("opacity", "1");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Windows CMD", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.Locator(".curl-command")).ToContainTextAsync("curl.exe --fail --user " + code + " \"");
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy command", Exact = true }).ClickAsync();
        var cmdCommand = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(await dialog.Locator(".curl-command").TextContentAsync(), cmdCommand);
        Assert.DoesNotContain(code + ":" + pin, cmdCommand);
        if (OperatingSystem.IsWindows())
        {
            // Exercise CMD's real argument parsing. Supply synthetic credentials
            // through curl's stdin config so no PIN appears in process arguments.
            var info = new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/d /s /c \"" + cmdCommand + " --silent --show-error --max-time 10 --config -\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync($"user = \"{code}:{pin}\"");
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) process.Kill(true); }
            Assert.True(process.ExitCode == 0, await errors);
            Assert.Equal("echo ready", await output);
        }
        await dialog.GetByRole(AriaRole.Button, new() { Name = "PowerShell", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.Locator(".curl-command")).ToContainTextAsync("curl.exe --fail --user " + code + " '");
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public async Task Browser_engines_copy_preserve_maximum_text_and_escape_html(string engine)
    {
        var type = engine switch { "firefox" => fixture.Playwright.Firefox, "webkit" => fixture.Playwright.Webkit, _ => fixture.Playwright.Chromium };
        var serverUri = new Uri(fixture.Url);
        var insecureOrigin = engine == "chromium" && serverUri.Scheme == "http" && serverUri.IsLoopback;
        var origin = insecureOrigin ? new UriBuilder(serverUri) { Host = "shareit.test" }.Uri.GetLeftPart(UriPartial.Authority) : fixture.Url.TrimEnd('/');
        await using var browser = await type.LaunchAsync(new()
        {
            Headless = true, Timeout = 30000,
            Args = insecureOrigin ? ["--host-resolver-rules=MAP shareit.test 127.0.0.1"] : []
        });
        await using var context = await browser.NewContextAsync();
        // Chromium exercises real insecure HTTP; the other engines simulate the missing API.
        if (!insecureOrigin)
            await context.AddInitScriptAsync("Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });");
        var page = await Page(context, origin);
        if (insecureOrigin) Assert.False(await page.EvaluateAsync<bool>("window.isSecureContext"));
        Assert.True(await page.EvaluateAsync<bool>("navigator.clipboard === undefined"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Create session", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync();
        var code = (await dialog.Locator(".credentials strong").First.TextContentAsync())!;
        await AssertCopiedByPasting(page, dialog.Locator(".credentials button").First, code);
        await page.GetByRole(AriaRole.Button, new() { Name = "Open workspace" }).ClickAsync();
        await page.Locator(".workspace").WaitForAsync();
        await AssertCopiedByPasting(page, page.Locator(".session-code"), code);
        await AssertCopiedByPasting(page, page.GetByRole(AriaRole.Button, new() { Name = "Copy link", Exact = true }), origin + "/?join=" + code);

        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Clipboard compatibility");
        const string content = "  مرحبًا 🌍 <tag> & café\n\tline two\n";
        await page.Locator("#text-content").FillAsync(content);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        var copyText = page.GetByRole(AriaRole.Button, new() { Name = "Copy text", Exact = true });
        await AssertCopiedByPasting(page, copyText, content);

        // Secure origins can still reject clipboard writes, for example when access is denied.
        await page.EvaluateAsync("Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new Error('Permission denied for test')) } });");
        await AssertCopiedByPasting(page, page.Locator(".session-code"), code);
        await AssertCopiedByPasting(page, copyText, content);
        await page.Locator(".text-card").GetByRole(AriaRole.Button, new() { Name = "curl", Exact = true }).ClickAsync();
        var command = (await dialog.Locator(".curl-command").TextContentAsync())!;
        await AssertCopiedByPasting(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Copy command", Exact = true }), command);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Add text", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Add text", Exact = true })).ToBeFocusedAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add text", Exact = true }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Maximum text");
        const string prefix = "<img src=x onerror=alert('unsafe')>\n";
        var maximumContent = prefix + new string('"', 65536 - Encoding.UTF8.GetByteCount(prefix));
        await page.Locator("#text-content").FillAsync(maximumContent);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".text-card")).ToHaveCountAsync(2);
        Assert.Empty(await page.Locator(".text-card img").AllAsync());
        var raw = await page.EvaluateAsync<string>("""
            async url => {
                const response = await fetch(url);
                if (!response.ok) throw new Error(`Raw text request failed: ${response.status}`);
                return response.text();
            }
            """, $"/api/v1/sessions/{code}/texts/2/raw");
        Assert.Equal(maximumContent, raw);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Keep working", Exact = true })).ToBeFocusedAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".closed-state")).ToBeVisibleAsync();
    }

    private static async Task AssertCopiedByPasting(IPage page, ILocator button, string expected)
    {
        await button.ClickAsync();
        await Assertions.Expect(page.Locator("dialog[open] .dialog-toast, body:not(:has(dialog[open])) #global-toast")).ToHaveTextAsync("Copied to clipboard.");
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveCountAsync(0);
        await Assertions.Expect(button).ToBeFocusedAsync();
        await page.EvaluateAsync("""
            () => {
                const input = document.createElement('textarea');
                input.id = 'clipboard-paste-check';
                (document.querySelector('dialog[open]') || document.body).appendChild(input);
                input.focus({ preventScroll: true });
            }
            """);
        try
        {
            await page.Keyboard.PressAsync("ControlOrMeta+V");
            await Assertions.Expect(page.Locator("#clipboard-paste-check")).ToHaveValueAsync(expected);
        }
        finally
        {
            await page.EvaluateAsync("document.getElementById('clipboard-paste-check')?.remove()");
        }
    }
}
