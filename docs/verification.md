# Verification

Verified on 24 September 2026.

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
