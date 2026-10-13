# Queue metrics and request traces

The dashboard's **Queue health** panel follows the experiment run filter. Its source is `GET /api/metrics`, optionally with `?runId=<UUID>`. Values come from committed PostgreSQL rows, so restarting the worker does not reset them.

| Metric | Meaning |
| --- | --- |
| Waiting | Pending and Retrying deliveries, including delayed retries and paused runs. |
| In flight | Deliveries with an active claim; an interrupted claim remains here until lease recovery. |
| Oldest queued | Age since creation of the oldest Pending or Retrying delivery. Empty queues show a dash. |
| Average delivery / 95th percentile | Creation-to-latest-success duration for currently Delivered deliveries. Includes waiting and any replay time. |
| Retries this cycle | Sum of attempts beyond the first in each delivery's current cycle. Replay resets that delivery's cycle count. |
| Completed / last 60s | Currently Delivered deliveries whose latest completion occurred in the last 60 seconds. This is a rolling count, not a lifetime total or requests per second. |

These count **deliveries**, so sending one event to two partners counts twice. The separate workspace totals remain global. Exhausted deliveries are excluded from successful duration metrics. Each refresh is a new database snapshot; sender metrics and receiver evidence are not a distributed transaction.

## Optional trace viewer

The normal stack needs no collector. It retains correlation IDs for inspection without sending telemetry anywhere. To export traces to a local Jaeger viewer:

```sh
docker compose -f compose.yaml -f compose.telemetry.yaml up -d --build --wait
```

Open http://localhost:16686 and run the two-partner experiment. Copy the **TRACE** ID from its event inspector into Jaeger's trace-ID search. Set `TRACE_PORT` if that local port is already occupied. Trace ingestion is reachable only inside the Compose network; the viewer is bound to localhost. Jaeger uses in-memory storage and loses traces when restarted; the lab retains its correlation IDs in PostgreSQL.

The creation request saves its W3C trace context alongside the immutable event. Each worker attempt restores that parent context, creates a `deliver order` span, and sends the trace context through HTTP instrumentation to the receiving service. Retries and destinations therefore share one trace even across worker restarts. Replay preserves that original trace context. Legacy events without stored context start a new trace on each attempt. Span metadata includes event and delivery IDs, destination, attempt number, and outcome; it does not record signed bodies or secrets.

Verify actual exported spans and their parent links:

```sh
pwsh -File scripts/verify-tracing.ps1
```

Use `-BaseUrl` and `-TraceUrl` for custom ports. The check creates a two-partner run and verifies its API creation span, four worker attempts, the HTTP send spans, and both receiver services in Jaeger's exported trace. It resumes the global worker, so run it on a dedicated test instance. CI executes this check after the backend, browser, and crash checks.

For an existing OpenTelemetry collector, configure `OTEL_EXPORTER_OTLP_ENDPOINT` on the lab and both partners. The default exporter uses OTLP/gRPC. Service names are `octo-lab`, `octo-partner-primary`, and `octo-partner-secondary`. Only write requests and webhook calls are traced; dashboard polling and health checks are excluded. The demo samples all traced requests. Collector outages do not change delivery success. Abrupt process exit can lose spans that have not been exported; the database records remain the proof of business completion.

Stop the optional stack with the same files:

```sh
docker compose -f compose.yaml -f compose.telemetry.yaml down
```
