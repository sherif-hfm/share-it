# Architecture

The application is a modular monolith. Web references Core and Infrastructure; Infrastructure references Core. Core has no ASP.NET, EF, or filesystem dependency.

## Project structure

```text
ShareIt.sln
src/
  ShareIt.Core/
    Configuration/      Session and storage limits
    Contracts/          Persistence, file storage, hashing, and notifications
    DTOs/               Application inputs and snapshots
    Models/             Sessions, text, files, grants, cleanup state
    Policies/           Access checks and quotas
    Services/           Session, text, upload, and purge workflows
  ShareIt.Infrastructure/
    BackgroundJobs/     Durable cleanup processing and wake signals
    Persistence/        EF Core adapter, context, and SQLite migrations
    Security/           Salted and peppered PIN hashing
    Storage/            Local filesystem adapter
  ShareIt.Web/
    Authentication/     Browser grants, curl Basic auth, attempt limits
    Components/
      Layout/           Application shell
      Pages/            Create/join and session workspace
      Shared/           Text cards, files, session header and dialogs
      UI/               Reusable controls, icons, and notifications
    Endpoints/          Browser HTTP actions and read-only curl API
    Resources/          English localization resources
    Services/           Live change notifications and circuit limits
    wwwroot/
      brand/            Original SVG identity
      css/              Design tokens, themes, responsive layouts
      fonts/            Self-hosted fonts with licenses
      js/               Clipboard, upload, theme, and dialog interactions
tests/
  ShareIt.Core.Tests/
  ShareIt.Infrastructure.Tests/
  ShareIt.Web.Tests/     HTTP integration and Playwright browser tests
deploy/                 Docker, Compose, and Caddy HTTPS configuration
docs/                   Architecture, lifecycle, API, and branding
```

`IShareItPersistence` operates on provider-neutral session aggregates. Its mutation callbacks are synchronous and execute inside one short database transaction. The singleton SQLite adapter serializes writes across the process, including quota reservation and cleanup intent. No network/file operation is performed inside a database transaction. Each operation uses a fresh factory-created DbContext. Internal IDs are application-generated, timestamps are UTC, and text versions are application-generated GUIDs.

SQLite contains sessions, text, browser grants, file metadata, and durable cleanup records. Files use generated storage keys outside the web root. The local adapter streams through a partial file and renames only on completion. Every mutation revalidates session authorization. A session work coordinator prevents a pending upload from recreating a purged blob.

Browser cookies identify anonymous browsers; per-session grants authorize them. HTTP Basic is selected for API GET requests and for the `/dav` surface. DAV always selects Basic, so browser cookies cannot upgrade drive access. Raw reads still validate that the authenticated session owns the requested item. PINs are peppered with HMAC-SHA256, then hashed with ASP.NET's salted PBKDF2 password hasher; the pepper is a separate random file. Failed PIN attempts are limited per source/session and per code. Rate-limit state is process-local and resets on restart, matching the single-instance deployment.

WebDAV uses a virtual namespace rather than mirrored directories. `WebDavResourceProvider` resolves session snapshots to stable item identities, portable friendly filenames, and read metadata; `WebDavEndpoints` and `WebDavProtocol` handle HTTP verbs, bounded XML, byte-range/conditional responses, and read-only policy. Files stream through the existing authorized file service; text comes from the same snapshot that supplies its version and byte length. No WebDAV code resolves user paths against physical storage. DAV receives its own per-IP request budget, while every credential check and session read continues enforcing existing access/lifecycle rules.

Future WebDAV writes must receive an explicit write capability rather than changing existing Basic readers. Text edits should map conditional validators to the existing text version checks; file operations must reuse quota reservations, cleanup coordination, and notifications through application services. Replacement, rename, dead-property storage, and lock behavior require their own write-support design. No dormant write implementation is included now.

Blazor receives session-change signals containing only immutable IDs, reloads authorized snapshots, and periodically rechecks expiry. Edit drafts are separate from snapshots. The UI never treats a connection's initial identity as sufficient authority for later operations. Circuit retention, count, and inbound activity are bounded.

Future PostgreSQL work: implement/test provider setup and migrations, export/import all related rows, switch during a maintenance window, and run persistence contract tests. Future object-storage work: implement streaming write/read and idempotent delete, migrate existing keys, and verify interruption/deletion behavior. Multiple application instances additionally require distributed coordination, notifications, and throttling; changing the database alone is insufficient.

Operational limits: the trusted server can read shared content. This is temporary sharing, not a password manager or end-to-end encrypted vault. File deletion is normal filesystem/database deletion, not a promise of forensic erasure from storage media. No uploaded content is executed or rendered as HTML.
