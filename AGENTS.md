# Working on this lab

This is a small, original reference implementation using fictional orders. Keep the workflow inspectable: API → PostgreSQL outbox → signed HTTP delivery → durable partner inbox. Keep proprietary code, client names, credentials, and real records out of the repository.

- `src/Lab` owns order creation, delivery scheduling, the worker, and the dashboard.
- `src/Partner` owns simulated failures, signature verification, and deduplicated business effects.
- `src/Contracts` owns the documented wire format, signature primitive, and retry policy.
- `src/Storage` owns the shared transactional migration runner; each host owns its SQL migration files.
- `src/Telemetry` owns shared trace registration; correlation must not change signed event bytes or delivery outcomes.
- Preserve order/outbox and receipt/business-effect transaction boundaries, immutable event bytes, claim-token fencing, bounded retries, and stable IDs on replay.
- Keep SQL parameterized. Do not add user-selected outbound URLs to this local demo.
- No frontend build pipeline is needed. The dashboard is static HTML/CSS and JavaScript modules served by ASP.NET Core.
- Run `docker compose --profile test run --build --rm tests` after behavior changes. Integration tests need real services; `dotnet test --filter Category=Unit` only covers isolated contracts.
- Run `pwsh -File scripts/verify-recovery.ps1` against the task-owned stack for crash/lease changes.
- For UI changes, exercise the controls, keyboard access, narrow widths, failure feedback, and replay in a browser. Put scratch screenshots in ignored `output/playwright/`.
- Do not change an existing schema file incompatibly without a documented migration or a clearly documented demo reset requirement.
