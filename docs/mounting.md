# Mount a Share-It session

Share-It provides a **read-only WebDAV drive** at `https://your-server/dav/your-code/`. Use the session's **Mount drive** button for its URL and platform commands. Rclone is the primary supported client; native WebDAV clients are not part of the acceptance matrix.

## Install the client

- **Windows:** install [rclone](https://rclone.org/downloads/) and [WinFsp](https://winfsp.dev/rel/). Run rclone in a regular terminal under the same user as Explorer, without elevating it to administrator.
- **Linux:** install rclone and the distribution's FUSE 3 package, including `fusermount3`.
- **macOS:** install the current rclone release. This guide uses `rclone nfsmount`, which uses macOS's built-in NFS client and does not need macFUSE. If your rclone lacks that command, update it. NFS is local to the mounting client; Share-It still communicates over HTTPS/WebDAV.

See [rclone's mounting guide](https://rclone.org/commands/rclone_mount/) for platform requirements. Mounting requires a reachable Share-It server and a trusted HTTPS certificate. For a private certificate authority, follow [offline deployment](offline-deployment.md); do not disable certificate verification. Explicit development/LAN HTTP configurations remain available but transmit Basic credentials without transport encryption.

## Connect without configuration

There is **no per-session `rclone config` step**:

1. Open the session's **Mount drive** dialog and choose your operating system.
2. Select **Copy mount command** and paste the entire block into your terminal.
3. Enter the session's four-digit PIN when prompted, preserving leading zeroes.

On Windows, use **PowerShell**. If you are currently in CMD, enter `powershell` first. The command finds rclone on PATH or as `rclone.exe` in the current folder. It mounts to `S:`; choose another unused letter by editing `S:` in the copied command. Linux and macOS mount under `$HOME/ShareIt-<session-code>`; their command runs a child Bash shell, so it also works when pasted into zsh.

The copied command already contains the server URL and session code. It uses rclone's [direct `:webdav:` connection](https://rclone.org/docs/#connection-strings), with `--config NUL` on Windows or `--config /dev/null` on Unix. **No named remote or PIN is saved in rclone's configuration.** View the generated script using **View command** in the dialog.

The PIN is read using a hidden prompt and passed to `rclone obscure` over standard input. The obscured value is supplied through the process environment, not command arguments. Windows restores the previous password environment variable when the command ends or fails; Unix keeps it in a child process. Rclone's obscuring is reversible and not an encryption boundary. The original PIN is not embedded in copied commands or shell history.

The PIN is not recoverable from the Share-It browser interface after its initial display. If you use a different WebDAV client, expand **Connection details for other clients** to copy the URL and use the session code/PIN for Basic authentication. Previously saved rclone remotes still work, but are not needed for this flow.

Keep the terminal running. The command uses `--read-only --dir-cache-time 15s --poll-interval 0`; **the server independently rejects writes**, even when the client omits `--read-only`.

The drive contains:

```text
texts/
  1-deployment.sh
  2-settings.json
files/
  1-document.pdf
```

Names start with the stable item number so duplicate titles/filenames remain distinct. Invalid Windows filename characters become underscores; Unicode is normalized, trailing dots/spaces are trimmed, and the final name is limited to 240 UTF-8 bytes. Ordinary file extensions up to 32 UTF-8 bytes are preserved. Text formats use `.txt`, `.sh`, `.ps1`, `.json`, or `.yaml`. Content bytes remain unchanged; a script extension does not cause the server to execute it.

Browser edits are visible after the 15-second directory cache expires and the item is refreshed/reopened. This is not push synchronization. Renaming a text card or changing its format changes its drive path. Apps may retain their own content caches; reopen the file or remount if needed. Existing server limits still apply.

When a session closes or expires, new requests fail authentication. A read already opened may finish, and content already copied or cached on the client cannot be remotely erased. The drive may remain mounted until you disconnect it.

## Disconnect

Close files and press **Ctrl+C** in the terminal running rclone. If a mount remains, run:

```bash
# Linux
fusermount3 -u "$HOME/ShareIt-w3r-yub"

# macOS
umount "$HOME/ShareIt-w3r-yub"
```

No saved connection needs to be removed. If you previously configured a named remote manually, you can remove that old entry through `rclone config`.

## Troubleshooting and server operation

- **401:** check the code, PIN, and session lifetime. **403** on a write is expected. Server writes and locks are unavailable in this release.
- **429:** respect `Retry-After`. DAV allows 600 requests per minute per source IP by default, separately from browser/API traffic. Configure `Limits__WebDavRequestsPerMinute` for capacity needs. Existing PIN attempt limits remain in force.
- **404 after an edit:** refresh the directory; the card may have been renamed, reformatted, or deleted.
- **Drive not visible on Windows:** run rclone without elevation, verify WinFsp installation, and select an unused drive letter.
- **Certificate error:** establish trust in the server's CA instead of bypassing TLS validation.

The existing reverse proxy forwards `/dav/` on the same host and port. No additional service, database migration, or storage copy is required. Proxies must preserve Authorization, DAV methods, Depth, conditional headers, and Range. See [HTTP interface](api.md) for the protocol contract and [verification](verification.md) for actual client test results.
