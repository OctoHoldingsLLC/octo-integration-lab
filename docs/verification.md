# Verification record

## Typography verification — October 12, 2026

Body text and inputs now use 16px at the default browser size, secondary text has a 14px minimum, and all text uses weight 400 or higher. Relative units respect browser font settings. Responsive layouts wrap without reducing type size.

The 20-case browser suite passed. A separate computed-style audit covered 320, 390, 768, 1024, and 1440px widths, open payloads, receiver timelines, permanent failures, circuit history, hover states, disabled controls, and connection errors. The lowest sampled text contrast was 5.03:1. At 200% text size and increased line, letter, word, and paragraph spacing, the sampled layouts had no document overflow or clipped controls. Tables and payloads retain their own scroll regions. Button backgrounds change immediately to avoid low contrast during transitions between disabled and active states.

## Expansion verification â€” October 12, 2026

The expanded lab passed 64 backend tests against real PostgreSQL and HTTP services: 30 isolated contract cases and 34 integration cases. Added coverage includes migration of a first-release database without changing event bytes, legacy payload delivery and replay, run idempotency and control isolation, deterministic failures, Retry-After seconds and all three HTTP-date formats, permanent rejection, a maximum 50-order burst, independent destinations, versioned receiver state, and circuit probe concurrency and stale-result fencing. Queue metrics are checked through claims, retries, completion, replay, and run filtering. Trace IDs are checked across failed attempts and receiver observations.

The browser suite passed 20 cases: all ten guided experiments, invalid burst feedback, lost order and experiment responses across reload, event links, search, status filtering, pagination, replay, keyboard operation, connection recovery, run-scoped metrics, and four viewport widths. Populated guided-run and timeline views were also checked at 320 and 1440 pixels with no detected WCAG A/AA violations. Automated accessibility checks do not replace a full manual assessment.

The optional Jaeger stack exported a single trace containing API creation, four worker delivery attempts, HTTP send spans, and both partner services. `verify-tracing.ps1` checked the actual exported parent links rather than just the presence of an ID. The real-crash script also checks that persisted trace correlation survives restarting the worker.

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
