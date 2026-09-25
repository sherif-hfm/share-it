using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using ShareIt.Web.Services;

namespace ShareIt.Web.Tests;

public class MountBootstrapTests
{
    [Theory]
    [InlineData("sh")]
    [InlineData("ps1")]
    public async Task Scripts_are_public_plain_text_without_session_data(string extension)
    {
        await using var app = new AppFactory();
        using var client = app.Browser();
        using var response = await client.GetAsync($"/ABC123/mount.{extension}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var script = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', script);
        Assert.EndsWith("\n", script);
        Assert.Contains("https://localhost/dav/abc-123/", script);
        Assert.Contains("--read-only", script);
        Assert.DoesNotContain("--webdav-pass", script);
        Assert.Contains(extension == "sh" ? "$(uname -s)" : "Read-Host 'Session PIN' -AsSecureString", script);

        // Creating a real session must not change the script's anonymous/data-free contract.
        using var owner = app.Browser();
        var session = await app.CreateSession(owner);
        var actual = await client.GetStringAsync($"/{session.Code}/mount.{extension}");
        Assert.DoesNotContain(session.Pin, actual);
        Assert.DoesNotContain(session.Id.ToString(), actual);
    }

    [Theory]
    [InlineData("sh", "bad")]
    [InlineData("ps1", "bad")]
    [InlineData("sh", "abc%27%3B123")]
    [InlineData("ps1", "abc%24123")]
    public async Task Invalid_codes_fail_download_instead_of_returning_a_script(string extension, string code)
    {
        await using var app = new AppFactory();
        using var client = app.Browser();
        using var response = await client.GetAsync($"/{code}/mount.{extension}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_code", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("sh", "192.0.2.10", "https")]
    [InlineData("ps1", "192.0.2.10", "https")]
    [InlineData("sh", "192.0.2.11", "http")]
    public async Task Script_urls_respect_path_base_and_only_trusted_forwarded_scheme(string extension, string source, string scheme)
    {
        await using var factory = new AppFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("ShareIt:TrustedProxy", "192.0.2.10"));
        var context = await app.Server.SendAsync(ctx =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(source);
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("share.example", 8443);
            ctx.Request.PathBase = "/shared space";
            ctx.Request.Path = $"/abc123/mount.{extension}";
            ctx.Request.Method = "GET";
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal(200, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        Assert.Contains($"{scheme}://share.example:8443/shared%20space/dav/abc-123/", await reader.ReadToEndAsync());
    }

    [BashSyntaxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloaded_bash_parses_and_bootstrap_quotes_the_url(bool bootstrap)
    {
        const string origin = "https://example.test/a'$(echo unexpected); #/";
        var command = MountCommands.Bootstrap(origin, "ABC123", MountPlatform.Linux);
        var script = bootstrap
            ? "function bash { \"$BASH\" \"$@\"; }; function curl { printf '%s\\n' \"$@\" >&2; }; " + command
            : MountCommands.Script(origin, "ABC123");
        var start = new ProcessStartInfo(BashSyntaxTheoryAttribute.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--noprofile"); start.ArgumentList.Add("--norc");
        if (!bootstrap) start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-s");
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(script); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await output);
            Assert.Equal(bootstrap ? "-fsSL\n" + MountCommands.ScriptUrl(origin, "ABC123", MountPlatform.Linux) + "\n" : "", await errors);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [BashSyntaxTheory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(22, 0)]
    [InlineData(18, 0)]
    [InlineData(0, 37)]
    public async Task Bash_bootstrap_propagates_download_and_script_failures_without_changing_caller_options(int downloadExit, int scriptExit)
    {
        var command = MountCommands.Bootstrap("https://example.test", "abc123", MountPlatform.Linux);
        var script = $$"""
            set +e
            set +o pipefail
            # Use the same Bash interpreter on Windows, where PATH may also contain WSL's bash.exe.
            function bash { "$BASH" "$@"; }
            function curl {
              if [[ {{downloadExit}} == 0 ]]; then printf 'exit %s\n' {{scriptExit}}; fi
              return {{downloadExit}}
            }
            {{command}} && printf 'continued\n'
            shareit_status=$?
            [[ ":$SHELLOPTS:" != *:pipefail:* ]] || exit 99
            exit "$shareit_status"
            """;
        var start = new ProcessStartInfo(BashSyntaxTheoryAttribute.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--noprofile"); start.ArgumentList.Add("--norc"); start.ArgumentList.Add("-s");
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(script); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var expectedExit = downloadExit != 0 ? downloadExit : scriptExit;
            Assert.Equal(expectedExit, process.ExitCode);
            Assert.Equal(expectedExit == 0 ? "continued\n" : "", await output);
            Assert.Equal("", await errors);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    public static IEnumerable<object[]> PowerShellCases()
    {
        var shells = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") };
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, "pwsh.exe")).FirstOrDefault(File.Exists);
        if (pwsh != null) shells.Add(pwsh);
        foreach (var shell in shells)
            foreach (var scenario in new[] { "success", "absent", "download", "drive", "pin", "prompt", "obscure", "mount", "rclone" })
                yield return [shell, scenario];
    }

    [WindowsShellTheory]
    [MemberData(nameof(PowerShellCases))]
    public async Task PowerShell_download_executes_with_hidden_pin_and_restores_callers_state(string shell, string scenario)
    {
        await using var app = new AppFactory();
        app.UseKestrel(0);
        using var client = app.CreateClient();
        var command = MountCommands.Bootstrap(client.BaseAddress!.AbsoluteUri, "abc123", MountPlatform.Windows);
        if (scenario == "download") command = command.Replace("abc-123", "bad");
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $env:RCLONE_WEBDAV_PASS = {{(scenario == "absent" ? "$null" : "'previous-value'")}}
            $shareItPlain = 'caller-value'
            $script:prompted = $false
            $script:mounted = $false
            $script:mountArgs = @()
            $script:during = $null
            $script:failure = ''
            function Get-Command { param($Name, $ErrorAction) if ('{{scenario}}' -ne 'rclone') { 'rclone' } }
            function Test-Path { param($LiteralPath) $false }
            function Get-PSDrive { param($Name, $ErrorAction) if ('{{scenario}}' -eq 'drive') { 'occupied' } }
            function Write-Host { param($Object) }
            function Read-Host {
              param([string]$Prompt, [switch]$AsSecureString)
              $script:prompted = $true
              if (-not $AsSecureString) { throw 'PIN prompt was not hidden.' }
              if ('{{scenario}}' -eq 'prompt') { throw 'Prompt unavailable.' }
              ConvertTo-SecureString '{{(scenario == "pin" ? "123" : "0047")}}' -AsPlainText -Force
            }
            function rclone {
              if ($args[0] -eq 'obscure') {
                if (($input -join '') -ne '0047' -or $args[1] -ne '-') { throw 'PIN must arrive through stdin.' }
                $global:LASTEXITCODE = if ('{{scenario}}' -eq 'obscure') { 1 } else { 0 }
                'obscured-value'
              } else {
                $script:mounted = $true
                $script:mountArgs = @($args)
                $script:during = $env:RCLONE_WEBDAV_PASS
                $global:LASTEXITCODE = if ('{{scenario}}' -eq 'mount') { 1 } else { 0 }
              }
            }
            try {
            {{command}}
            } catch { $script:failure = $_.Exception.Message }
            [PSCustomObject]@{ prompted=$script:prompted; mounted=$script:mounted; arguments=$script:mountArgs; during=$script:during; after=$env:RCLONE_WEBDAV_PASS; failure=$script:failure; scope=$shareItPlain; preference=$ErrorActionPreference.ToString() } | ConvertTo-Json -Compress
            """;
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await errors);
            var json = await output;
            Assert.DoesNotContain("0047", json); Assert.DoesNotContain("0047", await errors);
            using var result = JsonDocument.Parse(json);
            var root = result.RootElement;
            Assert.Equal(scenario == "absent" ? null : "previous-value", root.GetProperty("after").GetString());
            Assert.Equal("caller-value", root.GetProperty("scope").GetString());
            Assert.Equal("Continue", root.GetProperty("preference").GetString());
            Assert.Equal(scenario is not ("download" or "drive" or "rclone"), root.GetProperty("prompted").GetBoolean());
            Assert.Equal(scenario is "success" or "absent" or "mount", root.GetProperty("mounted").GetBoolean());
            var failure = root.GetProperty("failure").GetString()!;
            Assert.Equal(scenario is not ("success" or "absent"), failure.Length > 0);
            if (scenario == "drive") Assert.Contains("already in use", failure);
            if (scenario == "pin") Assert.Contains("four PIN digits", failure);
            if (scenario == "obscure") Assert.Contains("Could not prepare", failure);
            if (scenario == "mount") Assert.Contains("Could not mount", failure);
            if (root.GetProperty("mounted").GetBoolean())
            {
                Assert.Equal("obscured-value", root.GetProperty("during").GetString());
                var args = root.GetProperty("arguments").EnumerateArray().Select(x => x.ToString()).ToArray();
                Assert.Contains("S:", args); Assert.Contains("NUL", args); Assert.Contains("--network-mode", args);
                Assert.Contains("--read-only", args); Assert.Contains("abc-123", args);
                Assert.Contains(MountCommands.Url(client.BaseAddress.AbsoluteUri, "abc123"), args);
                Assert.DoesNotContain("--webdav-pass", args); Assert.DoesNotContain("obscured-value", args);
            }
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
