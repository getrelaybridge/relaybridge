# Milestone 2 — Queue Reliability

## Goal

Keep durably accepted mail recoverable through restarts, transient failures, and
local resource pressure without implementing Microsoft delivery.

## Implemented

- explicit `Queued`, `Delivering`, `RetryScheduled`, `Delivered`, and
  `PermanentFailure` states with centralized allowed transitions
- atomic single-row SQLite claiming using a conditional `UPDATE ... RETURNING`
- one to sixteen bounded workers, a coalescing semaphore wake-up, and a periodic
  safety poll; no task is created per message
- short metadata transactions around delivery, never across spool reads or
  provider waits
- success, transient, and permanent delivery results with safe bounded error
  metadata and an optional retry delay
- deterministic bounded exponential retry policy with jitter, maximum attempts,
  maximum age, and clamped retry hints
- startup recovery for stale claims, missing payloads, orphan promoted files,
  old temporary files, and delivered-payload cleanup remnants
- metadata-backed message-count and spool-byte admission plus free-disk reserve,
  including reservation growth while SMTP DATA streams
- delivered metadata retention with immediate payload deletion by default;
  permanent failures retain their payload for diagnosis
- queue/database/spool/capacity/worker health and inexpensive queue metrics
- graceful listener and worker shutdown with bounded forced cancellation
- automatic migration of the Milestone 1 queue schema

## Safe default before Microsoft delivery

The composed host leaves `Queue:Enabled` false because its placeholder delivery
provider cannot deliver mail. Startup reconciliation, admission control, durable
SMTP intake, health, and metrics still operate. Enabling the worker before a real
provider is registered would turn an intentional lack of delivery into exhausted
retries, so Milestone 4 must explicitly enable it when Exchange delivery exists.
Tests run the worker with scripted providers.

## Explicitly deferred

- Microsoft identity, MSAL, certificates, XOAUTH2, STARTTLS, and Exchange SMTP
- automatic retention expiry or administrator requeue/delete operations for
  permanent failures
- distributed leases, multiple processes, external schedulers, and metrics
  frameworks
- sophisticated submission/authentication rate windows
- claims of power-loss behavior beyond durable flush/SQLite guarantees and
  logical restart/failure-injection testing

## Exit criteria

State, claiming, retry, recovery, reconciliation, capacity, injected storage
failure, graceful shutdown, concurrency, schema migration, 100-message, and
multiple 10 MiB streaming tests pass together with all Milestone 1 tests. Release
build, formatting, package audit, and composed-host smoke verification must also
pass.

## Correctness audit

The post-completion audit tightened four confirmed boundaries without changing
the architecture:

- receive reservations now release from nested `finally` paths even if stream
  disposal or cleanup throws, and capacity arithmetic cannot overflow
- orphan deletion requires both a successful queue snapshot and a fresh positive
  database determination that the file has no owner; uncertainty preserves files
- result-persistence exceptions recover `Delivering` claims to `Queued` where the
  database remains usable, retaining the unavoidable post-provider duplicate risk
- retry expiration treats the exact maximum-age instant as terminal, consistently
  with the pre-scheduling age check

The audit also assigned SQLite schema version 2 and verified repeatable migration,
transactional rollback on failure, conservative count/byte accounting, active-temp
preservation, eight-way queued/due-retry claiming, and bounded shutdown behavior.
