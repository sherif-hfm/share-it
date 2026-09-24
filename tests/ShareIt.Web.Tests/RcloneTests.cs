using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ShareIt.Core.Services;

namespace ShareIt.Web.Tests;

public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (!System.IO.File.Exists(Environment.GetEnvironmentVariable("SHAREIT_RCLONE_PATH")))
            Skip = "Set SHAREIT_RCLONE_PATH to a local rclone executable to run the real-client test.";
    }
}

public sealed class RcloneTests
{
    [RcloneFact]
    public async Task Real_rclone_lists_stats_reads_seeks_and_obeys_server_read_only_and_expiry()
    {
        await using var app = new AppFactory();
        app.UseKestrel(0);
        using var browser = app.CreateClient();
        var session = await app.CreateSession(browser);
        var caller = await app.Owner(session.Id);
        var texts = app.Services.GetRequiredService<TextCardService>();
        var files = app.Services.GetRequiredService<FileService>();
        const string content = "  مرحبا 😀\r\n\tkeep exact text\n";
        await texts.SaveAsync(session.Id, caller, null, null, "note", content, "text");
        const string trickyTitle = "مرحبا & # %2F %25 café";
        await texts.SaveAsync(session.Id, caller, null, null, trickyTitle, "percent-path", "text");
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await files.UploadAsync(session.Id, caller, "data.bin", bytes.Length, new MemoryStream(bytes));
        await files.UploadAsync(session.Id, caller, "empty.bin", 0, new MemoryStream());

        // Only synthetic credentials enter the child environment. Nothing is written
        // to a user's rclone config, process arguments, logs, or the repository.
        var obscured = await RunAsync(["obscure", "-"], input: Encoding.UTF8.GetBytes(session.Pin + "\n"));
        Assert.Equal(0, obscured.ExitCode);
        var configuration = new Dictionary<string, string>
        {
            ["RCLONE_CONFIG_SMOKE_TYPE"] = "webdav",
            ["RCLONE_CONFIG_SMOKE_URL"] = new Uri(browser.BaseAddress!, $"/dav/{session.Code}/").AbsoluteUri,
            ["RCLONE_CONFIG_SMOKE_VENDOR"] = "other",
            ["RCLONE_CONFIG_SMOKE_USER"] = session.Code,
            ["RCLONE_CONFIG_SMOKE_PASS"] = Encoding.UTF8.GetString(obscured.Output).Trim()
        };
        async Task<byte[]> Read(params string[] arguments)
        {
            var result = await RunAsync(arguments, configuration);
            Assert.True(result.ExitCode == 0, result.Error);
            return result.Output;
        }

        var listing = Encoding.UTF8.GetString(await Read("lsf", "smoke:", "--recursive"));
        Assert.Contains("texts/1-note.txt", listing); Assert.Contains("files/1-data.bin", listing);
        Assert.Contains("texts/2-" + trickyTitle + ".txt", listing);
        using var stat = JsonDocument.Parse(await Read("lsjson", "smoke:texts/1-note.txt", "--stat"));
        Assert.Equal(Encoding.UTF8.GetByteCount(content), stat.RootElement.GetProperty("Size").GetInt64());
        Assert.False(stat.RootElement.GetProperty("IsDir").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetBytes(content), await Read("cat", "smoke:texts/1-note.txt"));
        Assert.Equal("percent-path", Encoding.UTF8.GetString(await Read("cat", "smoke:texts/2-" + trickyTitle + ".txt")));
        Assert.Equal(bytes, await Read("cat", "smoke:files/1-data.bin"));
        Assert.Equal(bytes[1048576..1048833], await Read("cat", "smoke:files/1-data.bin", "--offset", "1048576", "--count", "257"));
        Assert.Empty(await Read("cat", "smoke:files/2-empty.bin"));

        var sessions = app.Services.GetRequiredService<SessionService>();
        var before = await sessions.GetAsync(session.Code, caller);
        var write = await RunAsync(["rcat", "smoke:files/new.bin"], configuration, Encoding.UTF8.GetBytes("denied"));
        Assert.NotEqual(0, write.ExitCode); Assert.Contains("403", write.Error);
        Assert.Equal(before.Revision, (await sessions.GetAsync(session.Code, caller)).Revision);
        await texts.SaveAsync(session.Id, caller, 1, before.Texts[0].Version, "note", "updated", "text");
        Assert.Equal("updated", Encoding.UTF8.GetString(await Read("cat", "smoke:texts/1-note.txt")));
        await sessions.EndAsync(session.Id, caller, false);
        var ended = await RunAsync(["lsf", "smoke:"], configuration);
        Assert.NotEqual(0, ended.ExitCode); Assert.Contains("401", ended.Error);
    }

    private sealed record ToolResult(int ExitCode, byte[] Output, string Error);

    private static async Task<ToolResult> RunAsync(IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? configuration = null, byte[]? input = null)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("SHAREIT_RCLONE_PATH")!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Isolate the test from all ambient personal remotes and rclone flags.
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
        start.ArgumentList.Add("--retries"); start.ArgumentList.Add("1");
        start.ArgumentList.Add("--low-level-retries"); start.ArgumentList.Add("1");
        start.ArgumentList.Add("--contimeout"); start.ArgumentList.Add("5s");
        start.ArgumentList.Add("--timeout"); start.ArgumentList.Add("15s");
        if (configuration != null) foreach (var pair in configuration) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        try
        {
            using var output = new MemoryStream();
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
            var readError = process.StandardError.ReadToEndAsync();
            if (input != null) await process.StandardInput.BaseStream.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            await readOutput;
            return new(process.ExitCode, output.ToArray(), await readError);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
