# Security and operating boundary

This repository is a local educational lab. Docker Compose exposes only the dashboard/API on `127.0.0.1:5188`. The database and partner control API are internal to the Compose network. The default passwords and keys are published demo values.

The lab API intentionally has no user authentication. Do not expose its port to the public internet or put real data into it. Same-origin JSON checks reduce browser cross-origin writes; they are not authentication. Local processes can operate the controls. The partner management API has a separate control key, and webhook delivery uses a distinct signing secret.

Outbound delivery targets two fixed, operator-configured partner origins. Redirects, cookies, and proxies are disabled. Supporting arbitrary customer-supplied URLs would require a different threat model, including destination allowlisting, DNS rebinding defenses, authentication, tenant isolation, rate limits, key management, and audit retention.

Report security issues privately to **solutions@octoholdings.com** with reproduction steps using fictional data. Please do not publish secrets or exploit details in a public issue before discussing the report.
