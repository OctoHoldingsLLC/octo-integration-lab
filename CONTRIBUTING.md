# Contributing

Small, understandable experiments are welcome. Open an issue describing the behavior you want to teach and how a visitor would observe it. Keep new scenarios focused on the existing fictional workflow.

1. Start the stack with `docker compose up -d --build --wait`.
2. Make your change and run `docker compose --profile test run --build --rm tests`.
3. For delivery-lifecycle changes, run `pwsh -File scripts/verify-recovery.ps1`.
4. Include a before/after explanation and the verification you ran in your pull request.

Tests may update the lab's failure controls and create fictional orders. Use a dedicated local instance. New integration tests must verify observable behavior with real PostgreSQL and HTTP, including the failure path they introduce. Do not replace concurrency tests with in-memory database assertions.

Do not submit actual customer data, access tokens, proprietary source, or production endpoint addresses. Contributions are licensed under the repository's MIT license.
