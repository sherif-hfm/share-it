# Verification

Updated on 25 September 2026.

## WebDAV verification

The read-only drive adds 20 HTTP integration cases covering Basic-only authentication, generic challenges, session isolation and expiry, portable/Unicode/percent-escaped names, directory properties, unknown properties, XML/DTD and request-size rejection, exact bytes, HEAD, conditional/ranged reads, hidden pending/deleted files, and denied mutation methods. A full 150-item metadata burst fits the default DAV request budget; the browser/API budget and PIN lockout remain separate protections.

Protocol review fixes add nine regression cases for XML extension handling and WebDAV `If` conditions: GET/HEAD/PROPFIND/OPTIONS, strong ETags, negation, AND/OR lists, tagged same-session URLs, unknown lock tokens, escaped names, edits, malformed/oversized headers, and authentication/read-only precedence. A hosting regression also checks tagged public HTTPS URLs after trusted proxy forwarding.

The optional real-client integration passed using the official, SHA-256-verified **rclone v1.75.1 Windows amd64** executable against an isolated Kestrel HTTP test server. It exercised recursive listing, stat, Unicode/percent-containing paths, exact text and 2 MiB binary downloads, an offset read, an empty file, rejected upload without a client read-only flag, a browser-service text update, and denied access after closure. Synthetic credentials were supplied through standard input and a child-process-only environment; personal rclone configuration was not used.

To run this client check, set `SHAREIT_RCLONE_PATH` to an installed rclone executable:

```powershell
$env:SHAREIT_RCLONE_PATH = 'C:\tools\rclone\rclone.exe'
dotnet test tests/ShareIt.Web.Tests --filter FullyQualifiedName~RcloneTests
```

Without that variable, the test is explicitly skipped. The executable is a test prerequisite, not an application dependency.

The new Chromium browser test passed for platform selection, session-specific URL and command copying, read-only flags, credential handling, Escape/focus recovery, and mobile overflow. Desktop/mobile screenshots were visually inspected at `.artifacts/screenshots/mount-drive-desktop.png` and `mount-drive-mobile.png`.

The mounting dialog now generates direct `:webdav:` commands without a saved remote. The real-rclone check additionally reads exact text using only an ephemeral password environment variable and explicit URL/user arguments, with an empty configuration. Windows PowerShell 5.1 script tests replace the interactive prompt/native executable to verify hidden PIN input, stdin-only password obscuring, literal URL quoting, and environment restoration after success, obscuring failure, and mount failure. These script tests do not represent a native WinFsp mount.

| Native mount check | Status |
|---|---|
| Windows Explorer drive through WinFsp | Not run: WinFsp is not installed on this host |
| Linux FUSE mount | Passed: rclone v1.75.1 and FUSE 3 in an isolated Ubuntu container through Caddy HTTPS |
| macOS NFS mount | Not run: no macOS host available |

The Linux mount check used the fixed Release build in Production mode and the checked-in `deploy/offline/Caddyfile`, with an isolated private CA. The HTTP probe verified the certificate chain and hostname against that CA, and rclone used `--ca-cert` without bypassing TLS verification. Real browser actions created content, edited it without changing byte length, edited it to a different length, renamed it, deleted it, and closed the session. One continuously running FUSE mount with the generated command's 15-second directory cache detected each content/path change after refreshing the directory. Exact text and 2 MiB binary reads, seeked bytes, rejected writes, closed-session HTTP 401 responses, and clean native unmount all passed. Extended PROPFIND, false If conditions, Basic authentication, and ranged downloads also passed through HTTPS/Caddy. The browser context accepted the isolated certificate for UI automation; the separate HTTP and rclone checks verified trust.

The temporary containers, anonymous test volumes, and network were removed; the existing preview and its data were unchanged. This validates native Linux FUSE behavior inside Docker, not desktop file-manager integration. Windows Explorer and macOS Finder mounting remain unverified because this host has no WinFsp installation or macOS environment. No production deployment was performed. The ordinary real-rclone regression continues to use isolated loopback HTTP in Testing mode.

## Existing verification history

| Check | Result |
|---|---|
| Core rules | 12 tests passed |
| SQLite, storage, and lifecycle integration | 12 tests passed |
| HTTP authentication, antiforgery, quotas, downloads, and retry timing | 8 tests passed |
| Browser acceptance | 7 tests passed against the published Linux Docker image |
| Release Docker build | Passed; runs as the non-root `app` user |
| Docker Compose configuration | Validated |
| Offline deployment and browser acceptance | Passed with external routes removed and browser requests restricted to the app origin |

The original 39 tests passed. Browser coverage includes Chromium, Firefox, and WebKit on the development machine. Scenarios cover two-browser sharing, file uploads, live closure, conflicting edits, maximum-size text, HTML escaping, dialog keyboard focus, and responsive dark/light layouts. Regression coverage includes copying the current conflicted draft, denied clipboard access with Escape/focus recovery, and dialog-local copy feedback. Screenshots are generated in `.artifacts/screenshots/`.

The curl browser regression also selects Windows CMD, copies its command, and on Windows executes it through real `cmd.exe` and `curl.exe` against an isolated session. The PIN is supplied through standard input configuration during this test, not embedded in the generated command.

Clipboard compatibility checks verify real copy/paste in Chromium, Firefox, and WebKit when the modern Clipboard API is missing or rejects a write. Chromium uses an insecure HTTP hostname against the local fixture to reproduce LAN access; the other engines simulate the missing API. Coverage includes credentials, session codes and links, Unicode/multiline text, curl commands, and restoring the draft editor's focus. The manual text box and Escape recovery are checked when both automatic copy methods fail.

For the clipboard change, all eight browser scenarios passed across separate runs. Combined runs encountered HTTP 429 responses from the production request limiter during page startup; the three engine scenarios passed together in a fresh fixture. Production limits were unchanged.

Lifecycle checks include immediate deletion, closed-session retention until expiry, durable retry after a storage failure, abandoned uploads, simultaneous quota reservations, and cancellation of an active upload when its session closes. Additional tests cover independent cleanup during a stalled upload, bounded storage cancellation, expiry changes during retry backoff, retry reset after recovery, and migration of older cleanup records.

The additional offline browser regression passed against both the development fixture and the Linux Production stack using `deploy/offline/compose.yml`. The latter used private HTTPS on a loopback test port. Both app and proxy had no external IP route; external DNS failed, and browser HTTP/WebSocket requests were restricted to the app origin. A fresh Caddy CA and server certificate were created after proxy egress was blocked. Real Windows curl verified the certificate chain/hostname using the exported CA (plus Schannel's best-effort revocation option) and downloaded exact text and binary file bytes. Two cold browser contexts created/joined a session, shared text live, uploaded/downloaded a file, loaded both local fonts, and received session closure. The background worker removed the synthetic uploaded file after closure.

The temporary route removal was confined to test-container network namespaces; it is not a production firewall recipe. Browser test contexts allow the private test certificate; the separate curl checks verify TLS trust. No host certificate trust or host routing was changed. A real second LAN machine and its firewall/trust configuration still need to be provisioned as described in [offline deployment](offline-deployment.md).

Public DNS and public ACME certificate issuance have not been deployed or exercised. Deployment configuration is in `deploy/`; persistent volumes and Caddy are configured there.

The local review container is named `shareit-local-preview` and serves `http://localhost:5180`. Its existing database, files, and keys were preserved when applying the fixes. It now uses the named volumes `shareit_preview_data` and `shareit_preview_secrets`; `docker stop shareit-local-preview` keeps them and `docker start shareit-local-preview` resumes the preview. Session expiry and automatic deletion continue normally while the app is running. Use the production Compose configuration in the README for public deployment.

The Docker restore explicitly includes ASP.NET framework web assets before Razor sources are copied. The app maps static asset endpoints and references the framework script through `Assets`, following Microsoft's [Blazor static asset guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/static-files?view=aspnetcore-10.0).
