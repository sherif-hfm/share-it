using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ShareIt.Web.Services;

namespace ShareIt.Web.Tests;

public sealed class WindowsShellTheoryAttribute : TheoryAttribute
{
    public WindowsShellTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This test executes Windows PowerShell 5.1.";
    }
}

public sealed class BashSyntaxTheoryAttribute : TheoryAttribute
{
    public static string Executable => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/bash";
    public BashSyntaxTheoryAttribute()
    {
        if (!File.Exists(Executable)) Skip = "Bash is required to check the generated Unix scripts.";
    }
}

public sealed class MountCommandsTests
{
    [BashSyntaxTheory]
    [InlineData(MountPlatform.Linux)]
    [InlineData(MountPlatform.MacOS)]
    public async Task Unix_command_parses_including_nested_url_quoting(MountPlatform platform)
    {
        var command = MountCommands.Mount("https://example.test/a'$(echo unexpected); #/", "abc123", platform);
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
            // Parse the outer quoting, then syntax-check (never run) the child script.
            await process.StandardInput.WriteLineAsync("\"$BASH\" -n -c " + command["bash -c ".Length..]);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await errors); Assert.Equal("", await output);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [WindowsShellTheory]
    [InlineData("none")]
    [InlineData("mount")]
    [InlineData("obscure")]
    public async Task PowerShell_command_prompts_hides_pin_quotes_url_and_restores_environment(string failure)
    {
        // Test the actual copied script in PowerShell 5.1, substituting only the
        // interactive prompt and native mount (which otherwise requires WinFsp).
        const string origin = "https://example.test/a'$(throw 'injected'); #/";
        var command = MountCommands.Mount(origin, "ABC123", MountPlatform.Windows);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $env:RCLONE_WEBDAV_PASS = 'previous-value'
            $script:mounted = $false
            $script:mountArgs = @()
            $script:failure = ''
            function Read-Host {
              param([string]$Prompt, [switch]$AsSecureString)
              if (-not $AsSecureString) { throw 'PIN prompt was not hidden.' }
              ConvertTo-SecureString '0047' -AsPlainText -Force
            }
            function rclone {
              if ($args[0] -eq 'obscure') {
                if (($input -join '') -ne '0047' -or $args[1] -ne '-') { throw 'PIN must arrive through stdin.' }
                $global:LASTEXITCODE = if ('{{failure}}' -eq 'obscure') { 1 } else { 0 }
                'obscured-value'
              } else {
                $script:mounted = $true
                $script:mountArgs = @($args)
                $script:during = $env:RCLONE_WEBDAV_PASS
                $global:LASTEXITCODE = if ('{{failure}}' -eq 'mount') { 1 } else { 0 }
              }
            }
            try {
            {{command}}
            } catch { $script:failure = $_.Exception.Message }
            [PSCustomObject]@{ mounted=$script:mounted; arguments=$script:mountArgs; during=$script:during; after=$env:RCLONE_WEBDAV_PASS; failure=$script:failure } | ConvertTo-Json -Compress
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Do not let a PowerShell 7 test launcher redirect 5.1 to incompatible modules.
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await errors);
            var json = await output;
            Assert.DoesNotContain("0047", json); Assert.DoesNotContain("0047", await errors);
            using var result = JsonDocument.Parse(json);
            Assert.Equal("previous-value", result.RootElement.GetProperty("after").GetString());
            Assert.True((failure != "obscure") == result.RootElement.GetProperty("mounted").GetBoolean(), json);
            Assert.Equal(failure != "none", result.RootElement.GetProperty("failure").GetString()!.Length > 0);
            if (failure == "obscure") Assert.Contains("Could not prepare", result.RootElement.GetProperty("failure").GetString());
            if (failure == "mount") Assert.Contains("Could not mount", result.RootElement.GetProperty("failure").GetString());
            if (failure == "obscure") return;
            Assert.Equal("obscured-value", result.RootElement.GetProperty("during").GetString());
            var arguments = result.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.ToString()).ToArray();
            Assert.Contains(":webdav:", arguments); Assert.Contains("NUL", arguments);
            Assert.Contains("--read-only", arguments); Assert.Contains("abc-123", arguments);
            Assert.Contains(MountCommands.Url(origin, "abc123"), arguments);
            Assert.DoesNotContain("--webdav-pass", arguments); Assert.DoesNotContain("obscured-value", arguments);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
