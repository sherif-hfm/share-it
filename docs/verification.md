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

All 39 tests pass. Browser coverage includes Chromium, Firefox, and WebKit on the development machine. Scenarios cover two-browser sharing, file uploads, live closure, conflicting edits, maximum-size text, HTML escaping, dialog keyboard focus, and responsive dark/light layouts. Regression coverage includes copying the current conflicted draft, denied clipboard access with Escape/focus recovery, and dialog-local copy feedback. Screenshots are generated in `.artifacts/screenshots/`.

The curl browser regression also selects Windows CMD, copies its command, and on Windows executes it through real `cmd.exe` and `curl.exe` against an isolated session. The PIN is supplied through standard input configuration during this test, not embedded in the generated command.

Lifecycle checks include immediate deletion, closed-session retention until expiry, durable retry after a storage failure, abandoned uploads, simultaneous quota reservations, and cancellation of an active upload when its session closes. Additional tests cover independent cleanup during a stalled upload, bounded storage cancellation, expiry changes during retry backoff, retry reset after recovery, and migration of older cleanup records.

Public DNS, ACME certificate issuance, and production HTTPS have not been deployed or exercised. The packaged browser verification uses a loopback-only HTTP development environment. Deployment configuration is in `deploy/`; persistent volumes and Caddy are configured there.

The local review container is named `shareit-local-preview` and serves `http://localhost:5180`. Its existing database, files, and keys were preserved when applying the fixes. It now uses the named volumes `shareit_preview_data` and `shareit_preview_secrets`; `docker stop shareit-local-preview` keeps them and `docker start shareit-local-preview` resumes the preview. Session expiry and automatic deletion continue normally while the app is running. Use the production Compose configuration in the README for public deployment.

The Docker restore explicitly includes ASP.NET framework web assets before Razor sources are copied. The app maps static asset endpoints and references the framework script through `Assets`, following Microsoft's [Blazor static asset guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/static-files?view=aspnetcore-10.0).
