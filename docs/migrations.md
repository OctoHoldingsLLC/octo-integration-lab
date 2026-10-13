# Database migrations

Each service applies embedded SQL migrations in a transaction before accepting traffic. A PostgreSQL advisory lock serializes startup. `schema_versions` records the service, resource name, SHA-256 checksum, and application time. Changing a recorded script makes startup fail explicitly.

The original `schema.sql` files are immutable baselines. Existing first-release volumes are adopted by executing those idempotent baselines and recording their checksums; existing records and event bytes remain intact. New installations use the same path. Line endings are normalized when hashing so Windows and Linux builds agree.

Add schema changes as sequential files in the owning service's `Migrations` folder, for example `002_experiments.sql`. They are embedded and applied in ordinal filename order. Never change an applied migration. Add a correcting migration instead. Failed migrations roll back together with the ledger entries.

Back up the volume before upgrading a valuable local dataset. Rollbacks mean restoring that backup with the previous application version; automatic down migrations are intentionally not provided. A destructive demo reset remains an explicit `docker compose down --volumes` action.
