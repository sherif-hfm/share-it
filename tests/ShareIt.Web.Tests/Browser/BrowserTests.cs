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
    private async Task<IPage> Page(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.Url);
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
    public async Task Two_browsers_share_text_files_and_receive_session_closure()
    {
        await using var first = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        await using var second = await fixture.Browser.NewContextAsync();
        var page = await Page(first);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "home-dark.png"), FullPage = true });
        var session = await Create(page);
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
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "workspace-populated.png"), FullPage = true });
        await other.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await other.GetByRole(AriaRole.Dialog).WaitForAsync();
        await other.ScreenshotAsync(new() { Path = Path.Combine(fixture.Artifacts, "end-session.png"), FullPage = true });
        await other.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".closed-state")).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.Empty(await page.Locator(".text-card").AllAsync());
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
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy my draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveValueAsync(latestDraft);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".clipboard-fallback")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#text-content")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator("#text-content")).ToHaveValueAsync(latestDraft);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save as a new card" }).ClickAsync();
        await Assertions.Expect(page.Locator(".text-card")).ToHaveCountAsync(2);
        var raw = await context.APIRequest.GetAsync(fixture.Url + $"/api/v1/sessions/{session.Code}/texts/2/raw");
        Assert.Equal(latestDraft, await raw.TextAsync());
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
    public async Task Browser_engines_preserve_maximum_text_and_escape_html(string engine)
    {
        var type = engine switch { "firefox" => fixture.Playwright.Firefox, "webkit" => fixture.Playwright.Webkit, _ => fixture.Playwright.Chromium };
        await using var browser = await type.LaunchAsync(new() { Headless = true, Timeout = 30000 });
        await using var context = await browser.NewContextAsync();
        var page = await Page(context);
        var session = await Create(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" })).ToBeFocusedAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add your first text" }).ClickAsync();
        await page.GetByLabel("TITLE", new() { Exact = true }).FillAsync("Maximum text");
        const string prefix = "<img src=x onerror=alert('unsafe')>\n";
        var content = prefix + new string('"', 65536 - Encoding.UTF8.GetByteCount(prefix));
        await page.Locator("#text-content").FillAsync(content);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save text", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".text-card")).ToHaveCountAsync(1);
        Assert.Empty(await page.Locator(".text-card img").AllAsync());
        var raw = await context.APIRequest.GetAsync(fixture.Url + $"/api/v1/sessions/{session.Code}/texts/1/raw");
        Assert.True(raw.Ok);
        Assert.Equal(content, await raw.TextAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Keep working", Exact = true })).ToBeFocusedAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "End session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".closed-state")).ToBeVisibleAsync();
    }
}
