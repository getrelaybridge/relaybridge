# Queue Durability and Recovery

## Acceptance boundary

SMTP DATA receives a final `250` only after RelayBridge has:

1. streamed the message to a unique file under `spool/incoming`;
2. called both asynchronous flush and `FileStream.Flush(true)`;
3. atomically moved the file on the same volume into `spool/pending`;
4. committed the envelope, recipients, stable message ID, size, file name, and
   initial `Queued` state in SQLite using `synchronous=FULL`.

A failure before the database commit returns a non-success SMTP response and
cleans the temporary or promoted file where possible. A crash after commit but
before the client receives `250` is inherently ambiguous: the sender may retry,
so duplicate delivery is possible. RelayBridge provides at-least-once-oriented
recovery, not exactly-once delivery.

## State and claiming

Eligible `Queued` or due `RetryScheduled` metadata is changed to `Delivering` by
one atomic conditional SQLite statement. The claim increments the attempt count
and records the attempt time. The connection closes before the provider opens or
streams the `.eml`, so no SQLite transaction is held during delivery.

Only these normal transitions exist:

```text
Queued ───────────────> Delivering
RetryScheduled ───────> Delivering
Delivering ───────────> Delivered
Delivering ───────────> RetryScheduled
Delivering ───────────> PermanentFailure
Delivering ───────────> Queued             (interruption recovery)
```

Successful delivery retains metadata and, by default, immediately deletes the
raw payload. Permanent failure retains metadata and payload for diagnosis.

## Retries

Transient results use exponential delay capped by configuration, with bounded
jitter. A provider retry hint is clamped between the initial and maximum delay.
No retry is scheduled after the attempt or message-age limit. Times are persisted
as UTC `DateTimeOffset` values, so restart does not reset the schedule or identity.

## Startup reconciliation

Reconciliation runs before workers start and is deterministic, idempotent,
top-directory-only, and bounded by a configured file count.

- `Delivering` rows are returned to `Queued`; the unchanged message ID limits
  accidental proliferation, but a crash after remote acceptance can still cause
  a duplicate.
- A payload-bearing non-delivered row whose `.eml` is missing becomes
  `PermanentFailure` with `MissingSpool` diagnostics and is never retried.
- A pending `.eml` is deleted and logged only after the full queue query succeeds
  and a fresh per-file database query positively confirms that its spool name has
  no owner. A database/query/cancellation failure aborts reconciliation before an
  unverified file can be deleted, is logged where applicable, and is retried on
  the next startup. In the only normal creation window for a confirmed orphan,
  the database commit—and therefore SMTP success—did not happen, so treating it
  as accepted mail would be unsafe.
- Old `.tmp` files are deleted only after the configured age; recent files are
  retained to avoid touching active sessions.
- A delivered payload already deleted before its metadata cleanup flag was saved
  is recorded as absent without being mislabeled as message loss.

All cleanup is restricted to RelayBridge's resolved incoming and pending spool
directories and expected `.tmp`/`.eml` patterns. An open/recent receive temporary
file is preserved by the age threshold; repeated cleanup is idempotent.

## Capacity and shutdown

Admission uses SQLite size/count metadata plus in-process reservations for active
DATA sessions; it does not recursively scan the spool. Every row whose persisted
`PayloadPresent` value is true consumes one message slot and its `SizeBytes`,
including `Queued`, `Delivering`, `RetryScheduled`, `PermanentFailure`, and a
`Delivered` row whose cleanup has not completed. Successful payload cleanup or
missing-payload reconciliation persists false and releases both. Orphan/temp files
have no row and therefore are not counted; active receive reservations cover that
pre-commit window. This is conservative if file deletion succeeds but its metadata
update fails: capacity remains charged until reconciliation repairs the flag.

Reservations are owned by the receive transaction and released in a `finally`
path after success, disconnect, malformed/oversized DATA, cancellation, or any
spool/database exception. Arithmetic checks reject impossible sizes without
overflow. Limits cover payload count, total payload bytes, and a minimum free-disk
reserve. A declared SMTP SIZE enables an early decision, while reservation growth
independently enforces the limit as DATA arrives. Capacity pressure returns
`452 4.3.1` and leaves previously accepted mail intact.

Shutdown stops new SMTP accepts first and lets current sessions finish within the
host deadline. The queue worker stops claiming immediately, then current provider
calls get the same bounded grace period. Remaining calls are cancelled; a
cooperative interrupted claim returns to `Queued`. Startup reconciliation repairs
any `Delivering` state left by abrupt termination.

## Schema migration and local health

SQLite `PRAGMA user_version` 3 identifies the Milestone 3 schema. Version 3 adds
the non-secret singleton Microsoft identity reference; no token or private-key
material is stored. A legacy Milestone 1 shape is detected, copied in a
transaction, and versioned only after
success. Repeated initialization is idempotent. A failed migration rolls back DDL
renames and fails startup with the original database preserved for diagnosis.

`/health` means local relay/queue health: database usability, spool writability,
free disk, capacity, and the configured local worker state. It does not mean
Microsoft identity or Exchange delivery readiness. Those states remain distinct
and no dashboard refresh initiates an SMTP connection.

## Operational limitations

Filesystem durability still depends on the operating system, storage device,
controller, and power-loss behavior honoring flush semantics. Reconciliation was
tested through logical restarts and injected I/O failures, not physical power
removal. Failed-payload expiry and administrator recovery controls are deferred.
A database failure while persisting a provider result returns the in-process claim
to `Queued` where possible. If the provider had already accepted the message, that
safe retry can duplicate delivery; exactly-once behavior remains impossible.
