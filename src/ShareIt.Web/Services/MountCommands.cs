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

    public static string Url(string baseUri, string code) => $"{baseUri.TrimEnd('/')}/dav/{Code(code)}/";

    public static string Mount(string baseUri, string code, MountPlatform platform)
    {
        code = Code(code);
        var url = Url(baseUri, code);
        const string flags = "--read-only --dir-cache-time 15s --poll-interval 0";
        if (platform == MountPlatform.Windows)
            return $$"""
                & {
                  $ErrorActionPreference = 'Stop'
                  $shareItRclone = if (Get-Command rclone -ErrorAction SilentlyContinue) { 'rclone' } elseif (Test-Path -LiteralPath '.\rclone.exe') { '.\rclone.exe' } else { throw 'Install rclone or open PowerShell in its folder.' }
                  $shareItPreviousPass = $env:RCLONE_WEBDAV_PASS
                  $shareItPin = Read-Host 'Session PIN' -AsSecureString
                  try {
                    $shareItPlain = [System.Net.NetworkCredential]::new('', $shareItPin).Password
                    if ($shareItPlain -cnotmatch '^[0-9]{4}$') { throw 'Enter exactly four PIN digits.' }
                    $env:RCLONE_WEBDAV_PASS = $shareItPlain | & $shareItRclone obscure - --config NUL
                    $shareItPlain = $null
                    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the session credentials.' }
                    & $shareItRclone mount :webdav: S: --config NUL --webdav-url {{PowerShellQuote(url)}} --webdav-user {{code}} --webdav-vendor other {{flags}} --network-mode
                    if ($LASTEXITCODE -ne 0) { throw 'Could not mount the session. Check the rclone message above.' }
                  } finally {
                    $env:RCLONE_WEBDAV_PASS = $shareItPreviousPass
                    $shareItPlain = $null
                    $shareItPin.Dispose()
                  }
                }
                """;
        var directory = $"\"$HOME/ShareIt-{code}\"";
        // A child Bash works from Bash or zsh, keeps secrets out of the parent
        // environment, and reads the PIN from the terminal rather than pasted lines.
        var script = $$"""
            set -euo pipefail
            command -v rclone >/dev/null || { printf 'Install rclone first.\n' >&2; exit 1; }
            read -r -s -p 'Session PIN: ' shareit_pin </dev/tty
            printf '\n' >/dev/tty
            [[ "$shareit_pin" =~ ^[0-9]{4}$ ]] || { printf 'Enter exactly four PIN digits.\n' >&2; exit 1; }
            shareit_password=$(printf '%s\n' "$shareit_pin" | rclone obscure - --config /dev/null)
            unset shareit_pin
            export RCLONE_WEBDAV_PASS="$shareit_password"
            unset shareit_password
            mkdir -p {{directory}}
            exec rclone {{(platform == MountPlatform.MacOS ? "nfsmount" : "mount")}} :webdav: {{directory}} --config /dev/null --webdav-url {{BashQuote(url)}} --webdav-user {{code}} --webdav-vendor other {{flags}}
            """;
        return "bash -c " + BashQuote(script);
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string BashQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string Unmount(string code, MountPlatform platform) => platform switch
    {
        MountPlatform.Windows => "Press Ctrl+C in the terminal running rclone.",
        MountPlatform.Linux => $"fusermount3 -u \"$HOME/ShareIt-{Code(code)}\"",
        _ => $"umount \"$HOME/ShareIt-{Code(code)}\""
    };
}
