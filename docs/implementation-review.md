# Implementation review — 24 September 2026

The main requested features are implemented. The review originally found the six issues recorded below. **All six have now been fixed**, and the expanded 39-test suite passes, including browser tests against the published Linux image.

## Fix verification

| Finding | Resolution | Regression coverage |
|---|---|---|
| Cleanup waits on unrelated uploads | Nonblocking lease acquisition, deferred retry, bounded parallel processing and cancellation deadlines | Busy upload and storage-timeout tests |
| Expiry overwrites deletion retries | Independent retry scheduling, reset after success, migration for older records | Expiry/backoff recovery and upgrade tests |
| Stale conflict draft copy | Clipboard handler reads the live textarea | Immediate copy after editing a conflicted draft |
| Fallback Escape discards draft | Escape is consumed; blur cleanup is detached before focus restoration | Denied clipboard permission, preserved editor and saved draft |
| Hidden modal copy feedback | Accessible status message inside each dialog | Credential and curl copy feedback tests |
| Incorrect Retry-After | Limiter metadata and actual credential-counter deadlines | Hourly creation, source lockout, and session lockout timing tests |

The original findings below describe the pre-fix implementation; their line numbers refer to that review snapshot.

## Findings

### P1 — A slow upload can block cleanup for unrelated sessions

`src/ShareIt.Infrastructure/BackgroundJobs/CleanupWorker.cs:20–22` processes cleanup candidates sequentially. `src/ShareIt.Core/Services/SessionPurgeService.cs:28` waits for a session's upload lease even for ordinary file deletion. If an upload stalls while an older file is awaiting deletion, that wait prevents the worker from processing other sessions' expiry or immediate purge requests.

Reproduction used the real cleanup worker and SQLite adapter: zero cleanup cycles completed while the upload was stalled; another session requesting immediate purge remained in storage. Cancelling the unrelated upload allowed the purge to complete. Closed and expired sessions still reject access; the defect delays physical cleanup.

Fix: skip and requeue busy sessions for ordinary cleanup, allow independent sessions to progress with bounded concurrency, and put deadlines around long-running work. Add a regression test with two independent sessions.

### P2 — Extending expiry postpones pending deletion retries

`src/ShareIt.Core/Services/SessionService.cs:74` overwrites `Cleanup.NextAttemptAtUtc` whenever expiry changes. `src/ShareIt.Infrastructure/Persistence/EfShareItPersistence.cs:63` uses that same timestamp to schedule failed file-deletion retries. Extending a session after a deletion failure therefore defers its retry until the new expiry. Attempts also remain nonzero after successful ordinary cleanup.

Reproduction moved a retry due in one minute to four hours later. After advancing two minutes, the deleted file was still `DeletePending` and was excluded from cleanup candidates.

Fix: separate expiry scheduling from deletion retry scheduling and reset retry state after successful cleanup. Test extension during backoff and another deletion after recovery.

### P2 — Conflict recovery copies an older draft

`src/ShareIt.Web/Components/Pages/Session.razor:46` puts the last server-rendered draft in a clipboard attribute. After a conflict, editing the textarea and immediately clicking **Copy my draft** copies that older attribute before the change completes its Blazor round trip.

Reproduction: the visible draft was `newest edited draft after conflict`, but the clipboard contained `first draft`.

Fix: read the live textarea value in the client clipboard handler. Test immediate copying after editing a conflicted draft; changing the binding event alone does not remove network latency.

### P2 — Clipboard fallback Escape discards the unsaved draft

`src/ShareIt.Web/wwwroot/js/shareit.js:35` removes the fallback textarea on Escape without preventing the native dialog's cancellation. That also closes the editor and invokes `CloseEditor` in `src/ShareIt.Web/Components/Pages/Session.razor:124`, which clears the draft.

Reproduction denied Clipboard API access and followed the displayed fallback instructions. Escape removed both the fallback and the editor.

Fix: prevent default and propagation for this Escape action, restore the editor's focus, and test with clipboard permission denied.

### P3 — Copy feedback is hidden behind dialogs

`src/ShareIt.Web/Components/Layout/MainLayout.razor:13` renders the toast outside native modal dialogs. Credential and curl copy feedback appears behind their backdrop and outside the active modal accessibility tree.

Fix: show clipboard feedback within the active dialog or use another accessible top-layer notification mechanism.

### P3 — Retry-After does not reflect the actual lockout

`src/ShareIt.Web/Program.cs:77` reports 60 seconds even when the one-hour session-creation limit is exhausted. Its exception handler reports 3,600 seconds for credential throttling, including a per-source lockout that lasts five minutes in `src/ShareIt.Web/Authentication/CredentialGuard.cs`.

Fix: derive the header from limiter metadata and the actual credential-counter expiry. Test both source and session-wide lockouts.

## Verification and remaining coverage

- At review time, all 32 existing tests passed despite these defects. After the fixes, all 39 tests pass: 12 core, 12 persistence/lifecycle, 8 HTTP, and 7 browser cases, including Chromium, Firefox, and WebKit against the Linux container.
- NuGet's vulnerability feed reported no known vulnerable packages among the solution's direct and transitive dependencies.
- Review did not identify a cross-session authorization bypass. Basic read-only scope, per-operation grants, antiforgery validation, generated storage keys, and proxy trust configuration were inspected. This is not an exhaustive security audit.
- The four reproduced defects now have permanent regression coverage, along with modal feedback, rate-limit timing, and the data migration.
- Public DNS/ACME/HTTPS deployment and sustained load/recovery testing remain unverified. Previous Linux-container browser verification used local development HTTP; it does not establish those production properties.
- Database and file storage have adapter boundaries. Another database or cloud-storage adapter remains future implementation work, consistent with the agreed first version.

Original reproduction artifacts are in `.artifacts/lifecycle-review/Program.cs` and `.artifacts/ui-review-conflict.cjs`. The hidden-feedback screenshot is `.artifacts/ui-review-copy.png`; corrected feedback is shown in `.artifacts/screenshots/dialog-copy-feedback.png`. Permanent tests are in `tests/ShareIt.Infrastructure.Tests/CleanupSchedulingTests.cs`, `tests/ShareIt.Web.Tests/ApiTests.cs`, and `tests/ShareIt.Web.Tests/Browser/BrowserTests.cs`.
