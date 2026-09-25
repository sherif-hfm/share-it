using ShareIt.Core.DTOs;

namespace ShareIt.Web.Services;

public enum MountPlatform { Windows, Linux, MacOS }

public static class MountCommands
{
    private const string Flags = "--read-only --dir-cache-time 15s --poll-interval 0";

    private static string Code(string code)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length == 0) throw new ArgumentException("A valid session code is required.", nameof(code));
        return SessionCode.Format(normalized);
    }

    public static string Url(string baseUri, string code) => $"{baseUri.TrimEnd('/')}/dav/{Code(code)}/";

    public static string ScriptUrl(string baseUri, string code, MountPlatform platform) =>
        $"{baseUri.TrimEnd('/')}/{Code(code)}/mount.{(platform == MountPlatform.Windows ? "ps1" : "sh")}";

    public static string Bootstrap(string baseUri, string code, MountPlatform platform)
    {
        var url = ScriptUrl(baseUri, code, platform);
        return platform == MountPlatform.Windows
            ? $"irm {PowerShellQuote(url)} -ErrorAction Stop | iex"
            : $"(set -o pipefail; curl -fsSL {BashQuote(url)} | bash)";
    }

    public static string Mount(string baseUri, string code, MountPlatform platform)
    {
        var script = Script(baseUri, code, platform);
        return platform == MountPlatform.Windows ? script : "bash -c " + BashQuote(script);
    }

    // A null platform produces the downloadable Bash script, which detects the client OS.
    public static string Script(string baseUri, string code, MountPlatform? platform = null)
    {
        code = Code(code);
        var url = Url(baseUri, code);
        if (platform == MountPlatform.Windows)
            return Lf($$"""
                & {
                  $ErrorActionPreference = 'Stop'
                  if ($env:OS -ne 'Windows_NT') { throw 'Use mount.sh with Bash on Linux or macOS.' }
                  $shareItRclone = if (Get-Command rclone -ErrorAction SilentlyContinue) { 'rclone' } elseif (Test-Path -LiteralPath '.\rclone.exe') { '.\rclone.exe' } else { throw 'Install rclone or open PowerShell in its folder.' }
                  $shareItDrive = 'S:'
                  if (Get-PSDrive -Name $shareItDrive.TrimEnd(':') -ErrorAction SilentlyContinue) { throw ('Drive ' + $shareItDrive + ' is already in use. Change $shareItDrive in the downloaded script, or copy Advanced: full command from Mount drive.') }
                  $shareItPreviousPass = $env:RCLONE_WEBDAV_PASS
                  $shareItPin = $null
                  try {
                    $shareItPin = Read-Host 'Session PIN' -AsSecureString
                    $shareItPlain = [System.Net.NetworkCredential]::new('', $shareItPin).Password
                    if ($shareItPlain -cnotmatch '^[0-9]{4}$') { throw 'Enter exactly four PIN digits.' }
                    $env:RCLONE_WEBDAV_PASS = $shareItPlain | & $shareItRclone obscure - --config NUL
                    $shareItPlain = $null
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:RCLONE_WEBDAV_PASS)) { throw 'Could not prepare the session credentials.' }
                    Write-Host "Mounting Share-It at $shareItDrive. Press Ctrl+C to disconnect."
                    & $shareItRclone mount :webdav: $shareItDrive --config NUL --webdav-url {{PowerShellQuote(url)}} --webdav-user {{code}} --webdav-vendor other {{Flags}} --network-mode
                    if ($LASTEXITCODE -ne 0) { throw 'Could not mount the session. Check the rclone message above and make sure WinFsp is installed.' }
                  } finally {
                    $env:RCLONE_WEBDAV_PASS = $shareItPreviousPass
                    $shareItPlain = $null
                    if ($null -ne $shareItPin) { $shareItPin.Dispose() }
                  }
                }
                """);
        var directory = $"\"$HOME/ShareIt-{code}\"";
        var os = platform switch { MountPlatform.Linux => "Linux", MountPlatform.MacOS => "Darwin", _ => "$(uname -s)" };
        // Define the complete function before running anything from a downloaded pipe.
        // /dev/tty leaves stdin available for the script and works with Bash 3.2.
        return Lf($$"""
            #!/usr/bin/env bash
            shareit_mount_session() {
              set -euo pipefail
              local shareit_os shareit_mount shareit_pin shareit_password
              shareit_os={{os}}
              case "$shareit_os" in
                Linux) shareit_mount=mount ;;
                Darwin) shareit_mount=nfsmount ;;
                *) printf 'Use mount.sh on Linux or macOS, or mount.ps1 in Windows PowerShell.\n' >&2; exit 1 ;;
              esac
              command -v rclone >/dev/null || { printf 'Install rclone first.\n' >&2; exit 1; }
              if [[ "$shareit_os" == Linux ]]; then
                command -v fusermount3 >/dev/null || { printf 'Install your distribution\x27s FUSE 3 package (fusermount3) first.\n' >&2; exit 1; }
              else
                rclone nfsmount --help >/dev/null 2>&1 || { printf 'Update rclone to a version with nfsmount support.\n' >&2; exit 1; }
              fi
              if ! { true </dev/tty; } 2>/dev/null; then
                printf 'Run this command in an interactive terminal to enter the session PIN.\n' >&2
                exit 1
              fi
              read -r -s -p 'Session PIN: ' shareit_pin </dev/tty || { printf '\nCould not read the session PIN.\n' >&2; exit 1; }
              printf '\n' >/dev/tty
              [[ "$shareit_pin" =~ ^[0-9]{4}$ ]] || { printf 'Enter exactly four PIN digits.\n' >&2; exit 1; }
              if ! shareit_password=$(printf '%s\n' "$shareit_pin" | rclone obscure - --config /dev/null); then
                unset shareit_pin
                printf 'Could not prepare the session credentials.\n' >&2
                exit 1
              fi
              unset shareit_pin
              [[ -n "$shareit_password" ]] || { printf 'Could not prepare the session credentials.\n' >&2; exit 1; }
              export RCLONE_WEBDAV_PASS="$shareit_password"
              unset shareit_password
              mkdir -p {{directory}}
              printf 'Mounting Share-It at %s. Press Ctrl+C to disconnect.\n' {{directory}}
              exec rclone "$shareit_mount" :webdav: {{directory}} --config /dev/null --webdav-url {{BashQuote(url)}} --webdav-user {{code}} --webdav-vendor other {{Flags}}
            }
            shareit_mount_session
            """);
    }

    private static string Lf(string value) => value.Replace("\r\n", "\n") + "\n";
    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string BashQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string Unmount(string code, MountPlatform platform) => platform switch
    {
        MountPlatform.Windows => "Press Ctrl+C in the terminal running rclone.",
        MountPlatform.Linux => $"fusermount3 -u \"$HOME/ShareIt-{Code(code)}\"",
        _ => $"umount \"$HOME/ShareIt-{Code(code)}\""
    };
}
