using System.Diagnostics;
using System.Text;
using ShareIt.Web.Services;

namespace ShareIt.Web.Tests;

public sealed class UnixTerminalFactAttribute : FactAttribute
{
    public UnixTerminalFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script"))
            Skip = "The piped Bash integration requires Linux and util-linux script (a pseudo-terminal).";
    }
}

public class UnixMountScriptTests
{
    [UnixTerminalFact]
    public async Task Downloaded_script_works_through_a_pipe_and_terminal()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, MountCommands.Script("https://example.test/a'$(echo unexpected); #/", "abc123"), new UTF8Encoding(false));
            var start = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Scripts", "mount-bootstrap-tests.sh"));
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(process.ExitCode == 0, (await output) + (await errors));
                Assert.Contains("All Bash mount scenarios passed.", await output);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
        finally { File.Delete(path); }
    }
}
