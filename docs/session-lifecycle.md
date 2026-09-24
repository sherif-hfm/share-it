# Session lifecycle

| Trigger | Access | Contents |
|---|---|---|
| Create | Code + PIN joins the session | Empty workspace; default expiry in one hour |
| Change expiry | Any granted browser, while active | At most 24 hours from creation |
| End, delete selected | Immediately closed | Text cleared and purge requested immediately |
| End, delete unselected | Immediately closed permanently | Inaccessible until the expiry fixed at closure |
| Expire | Immediately unavailable on every operation | Due for automatic purge |

All browser participants have equal permissions. Terminal credentials authorize reads only. An end request can upgrade an existing closure to immediate purge, but cannot downgrade a purge or reopen a session.

Cleanup intent lives in SQLite, not only in a memory queue. The hosted worker runs on startup, every minute, and when signalled. It rechecks expiry inside the mutation transaction so a stale candidate cannot purge an extended session. It removes text, PIN hashes and grants, cancels active uploads for a purge, deletes each blob and its metadata, and then removes the session. File keys remain durable until deletion succeeds. Missing blobs count as already deleted.

Cleanup skips busy upload leases and retries them after five seconds. Independent sessions can progress with up to four concurrent cleanup operations, each with a 30-second cancellation deadline. Ordinary file deletion leaves active uploads running. Storage adapters must honor cancellation. These defaults can be adjusted through `Limits__CleanupConcurrency` and `Limits__CleanupOperationTimeoutSeconds`.

Failures back off from one minute to one hour and are logged by exception type without sensitive content. Retry scheduling is independent of session expiry and is reset after successful cleanup. The next attempt timestamp and attempt count survive restarts. The `IndependentCleanupScheduling` migration corrects older records that used expiry as a retry deadline, without changing session expiry or deleting content. Startup also cleans pending uploads abandoned by a previous process. Pending and deleting blobs continue counting against storage quotas.

The browser says “deletion started,” not “deleted,” when the operation is only queued. Cleanup cannot retract a copy already downloaded to another machine.
