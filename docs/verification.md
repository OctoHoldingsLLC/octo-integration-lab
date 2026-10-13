# First-version verification

## Expansion verification — October 12, 2026

The expanded lab passed 57 backend tests against real PostgreSQL and HTTP services. Added coverage includes migration of a first-release database without changing event bytes, run idempotency and control isolation, deterministic failures, both Retry-After formats, permanent rejection, batches, independent destinations, versioned receiver state, and circuit probe concurrency and stale-result fencing.

The committed browser suite passed 18 cases: all ten guided experiments, invalid burst feedback, ambiguous API-response recovery across reload, event links and filtering, replay, keyboard operation, connection recovery, and four viewport widths. Populated guided-run and timeline views were also checked at 320 and 1440 pixels with no detected WCAG A/AA violations. Automated accessibility checks do not replace a full manual assessment.

The original verification below records the first release; it is retained as historical evidence.

Verified locally on October 10, 2026, with Docker Desktop, .NET 10, and PostgreSQL 17.

## Automated behavior

`docker compose --profile test run --build --rm tests` passed **36 tests**: 18 isolated contract/policy cases and 18 integration cases using real PostgreSQL and HTTP services. Coverage includes:

- Atomic order/outbox persistence, including a forced outbox failure and rollback.
- Concurrent repeated API requests and concurrent signed deliveries.
- Rejection of tampered, expired, malformed, and mismatched signatures.
- Timeouts, dropped connections, HTTP 503 recovery, exhaustion, and replay.
- Lost acknowledgements without duplicate receiver-side business effects.
- Competing claims, stale-result fencing, expired leases, and abandoned final attempts.
- JSON/origin enforcement, order validation, and partner control authorization.

`pwsh -File scripts/verify-recovery.ps1` separately passed with real container lifecycle operations:

1. Paused state and a pending event survived `docker compose restart lab` and delivered after resuming.
2. After the receiver committed an order, the sender was killed with SIGKILL. On restart it recovered the lease, recorded the original attempt as Interrupted, and received a duplicate acknowledgement. The receiver still had exactly one business-effect row for that event.

## Browser checks

Playwright exercised creating an order, repeating its API request, the lost-acknowledgement experiment, manual replay, recovery from 503s, pausing/resuming dispatch, event inspection, and keyboard operation of the payload disclosure. API retries left the order count unchanged; lost acknowledgements and manual replay increased duplicate receipts without increasing processed orders.

Widths of 320, 390, 768, and 1440 pixels had no document-level horizontal overflow. The delivery table has its own labeled, keyboard-focusable scroll region on narrow screens. The reviewed desktop and mobile screenshots use only fictional records.

After contrast corrections, axe-core reported zero detected WCAG A/AA violations in the sampled desktop and mobile states with a populated delivery timeline. Automated scans have incomplete checks and are not a claim of comprehensive accessibility conformance.

## Build and publication checks

The Release solution build and locked package restore succeeded with zero compiler warnings. C# whitespace verification, frontend formatting checks, and JavaScript syntax checks passed. Public source and documentation were checked for local links, accidental client references, and credential patterns. The repository's CI repeats the automated and real-crash checks on pushes and pull requests.
