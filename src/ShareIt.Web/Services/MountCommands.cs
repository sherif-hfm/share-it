using ShareIt.Core.DTOs;

namespace ShareIt.Web.Services;

public enum MountPlatform { Windows, Linux, MacOS }

public static class MountCommands
{
    private static string Code(string code)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length == 0) throw new ArgumentException("A valid session code is required.", nameof(code));
        return SessionCode.Format(normalized);
    }

    public static string RemoteName(string code) => "shareit-" + Code(code);
    public static string Url(string baseUri, string code) => $"{baseUri.TrimEnd('/')}/dav/{Code(code)}/";

    public static string Mount(string code, MountPlatform platform)
    {
        var remote = RemoteName(code) + ":";
        const string flags = "--read-only --dir-cache-time 15s --poll-interval 0";
        if (platform == MountPlatform.Windows) return $"rclone mount {remote} S: {flags} --network-mode";
        var directory = $"\"$HOME/ShareIt-{Code(code)}\"";
        return $"mkdir -p {directory}\nrclone {(platform == MountPlatform.MacOS ? "nfsmount" : "mount")} {remote} {directory} {flags}";
    }

    public static string Unmount(string code, MountPlatform platform) => platform switch
    {
        MountPlatform.Windows => "Press Ctrl+C in the terminal running rclone.",
        MountPlatform.Linux => $"fusermount3 -u \"$HOME/ShareIt-{Code(code)}\"",
        _ => $"umount \"$HOME/ShareIt-{Code(code)}\""
    };
}
