# Share-It

**Your workspace, across machines.** A temporary shared workspace for commands, text, configurations, and small files. Built with .NET 10, Blazor Interactive Server, EF Core, and SQLite.

## Run locally

Install a .NET 10 SDK. From the repository root:

```powershell
dotnet restore
dotnet run --project src/ShareIt.Web --environment Development --urls http://localhost:5180
```

Open http://localhost:5180. The SQLite schema migrates automatically. Local data, files, a random PIN pepper, and cookie-protection keys are created in `src/ShareIt.Web/.runtime/`, which is ignored by Git. HTTP is for local development; public deployment requires HTTPS.

Start a session, remember its code and four-digit PIN, and open the session from another browser. No accounts are required. Every participant can manage content or end the session. PINs are displayed once when creating the session and stored only as salted, peppered hashes.

## Verify

```powershell
dotnet test ShareIt.sln --filter "FullyQualifiedName!~BrowserTests"
# Install browser engines once (use pwsh on Linux/macOS):
powershell -ExecutionPolicy Bypass -File tests/ShareIt.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium firefox webkit
dotnet test tests/ShareIt.Web.Tests --filter "FullyQualifiedName~BrowserTests"
```

Browser tests cover Chromium, Firefox, and WebKit, start a local server with isolated test data, and generate review images in `.artifacts/screenshots/`. Set `SHAREIT_E2E_URL` to test an already-running local instance instead. Synthetic test content never contains real credentials.

## Terminal access

```bash
curl --fail --user w3r-yub 'https://share.example.com/api/v1/sessions/w3r-yub'
curl --fail --user w3r-yub 'https://share.example.com/api/v1/sessions/w3r-yub/texts/1/raw'
curl --fail --user w3r-yub 'https://share.example.com/api/v1/sessions/w3r-yub/files/1' --output config.json
```

Enter the session PIN when curl asks for a password. Use `curl.exe` in Windows PowerShell. Do not put the PIN in URLs or command arguments. Select **Bash / macOS**, **PowerShell**, or **Windows CMD** in the interface to copy the correct command for your terminal. Terminal access is read-only.

In Windows Command Prompt (`cmd.exe`), use double quotes; single quotes become part of the URL:

```bat
curl.exe --fail --user w3r-yub "https://share.example.com/api/v1/sessions/w3r-yub/texts/1/raw"
```

CMD file commands use a `download-<number>` filename when the original name contains variable expansions or characters Windows cannot use in a filename.

## Deploy with Docker

1. Point a domain to a Linux server and allow incoming TCP 80/443 (UDP 443 is optional).
2. Copy `deploy/.env.example` to `deploy/.env` and set the domain and certificate contact email.
3. Run `docker compose --env-file deploy/.env -f deploy/compose.yml up -d --build`.

Caddy supplies HTTPS and forwards WebSockets. Only Caddy exposes ports. The app runs as a non-root user, with persistent named volumes for data and secrets. Deploy exactly **one application instance** while using SQLite and in-process coordination. If the configured Docker subnet conflicts with your network, change both the subnet/Caddy address and `ShareIt__TrustedProxy` together.

Keep the data and secret volumes private. PIN verification requires its pepper; browser cookies require the Data Protection keyring. Losing either invalidates the corresponding access mechanism. Do not back up temporary contents unless you explicitly choose a retention policy. `docker compose down` retains data; `down -v` removes it.

## Configuration

Override `Limits` and `ShareIt` settings with normal .NET configuration, for example `Limits__MaxFileBytes` or `ShareIt__DataPath`. Defaults: 25 MB/file, 100 MB/session, 50 files, 100 text cards, 64 KiB/card, 2 MiB total text, and a 2 GiB global file budget. Pending uploads and undeleted files continue counting against quotas.

Sessions expire after one hour by default, with a 24-hour maximum from creation. End Session can request immediate deletion or retain inaccessible data until expiry. The background worker checks every minute and on startup; explicit deletion wakes it immediately. Failed deletions are retried with durable state. Requests are blocked immediately on expiry or closure even if physical cleanup is pending.

## Architecture and branding

- `ShareIt.Core`: provider-neutral rules, models, contracts, and services.
- `ShareIt.Infrastructure`: EF Core adapter, local file adapter, PIN hashing, and cleanup scheduling.
- `ShareIt.Web`: branded Blazor interface, HTTP authentication, endpoints, and notifications.
- See `docs/architecture.md`, `docs/session-lifecycle.md`, `docs/api.md`, and `docs/brand-and-ui.md`.
- Test coverage, verification results, and local preview details are in `docs/verification.md`.

The supported portability target is another relational database (PostgreSQL first), not an untested configuration-only switch. Provider changes need their own migrations and data transfer. Object-storage adapters implement `IFileStore` and must pass the same behavioral tests.

To create a schema change: `dotnet tool restore`, then `dotnet ef migrations add ChangeName --project src/ShareIt.Infrastructure --startup-project src/ShareIt.Web --output-dir Persistence/Migrations/Sqlite`.
