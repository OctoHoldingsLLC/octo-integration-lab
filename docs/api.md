# HTTP API

The lab listens at `http://localhost:5188`. Use `Content-Type: application/json` for every POST, including an empty `{}` replay body. Browser writes must come from the lab's own origin. The API is intentionally unauthenticated and bound to localhost by Compose.

## Lab endpoints

| Method | Path                               | Purpose                                                         |
| ------ | ---------------------------------- | --------------------------------------------------------------- |
| GET    | `/health`                          | Check the host and its database connection.                     |
| GET    | `/api/config`                      | Retry, timeout, and lease settings; no keys.                    |
| GET    | `/api/state`                       | Global counts, persisted pause state, filtered delivery page. |
| GET    | `/api/metrics`                     | Durable queue and delivery metrics, optionally scoped by `runId`. |
| POST   | `/api/orders`                      | Create an order and its outbox event atomically.                |
| GET    | `/api/deliveries/{eventId}`        | Immutable event payload and ordered attempt history.            |
| POST   | `/api/deliveries/{eventId}/replay` | Requeue a delivered or exhausted event.                         |
| POST   | `/api/worker`                      | Persist `{ "paused": true }` or `{ "paused": false }`.          |
| GET    | `/api/partner`                     | Read the partner's controls, counters, and latest 100 receipts. |
| POST   | `/api/partner`                     | Set `{ "mode": "healthy", "delayMs": 5000 }`.                   |

`GET /api/state` accepts `search` (reference, delivery UUID, or SKU), `status`, `page` (one-based), and `pageSize` (1–100). Defaults remain the newest 100 deliveries. `matched`, `page`, and `pageSize` describe the filtered result; the top-level counts remain global. The dashboard uses 20 rows per page.

`GET /api/deliveries/{eventId}` also returns a self-contained `delivery` summary, `rawPayload`, and `payloadHash` (SHA-256 of the UTF-8 body). `payload` is the parsed JSON for readable display. Only `rawPayload` represents the signed bytes. An event remains inspectable when it is outside the current page or filters. Dashboard links use `?event=<UUID>`.

The route parameter is a **delivery UUID**. Primary deliveries preserve the event UUID for compatibility. Secondary deliveries have their own UUID while `delivery.eventId` and `payload.id` retain the signed event UUID. Details include `receiverAvailable` and independently fetched `receiver` observations. Each attempt and receiver observation exposes its `traceId` when available. The state endpoint also accepts `runId` to restrict its page to a specific experiment. `delivery.destination` is `primary` or `secondary`; URLs are never accepted from browser input.

`GET /api/metrics?runId=<UUID>` returns `runId`, `queued`, `inFlight`, `oldestQueuedSeconds`, `averageDeliverySeconds`, `p95DeliverySeconds`, `currentCycleRetries`, and `completedLastMinute`. Omit `runId` for global metrics. Duration/age fields are null when there are no matching deliveries. See [metric definitions and trace setup](observability.md).

### Guided experiment endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/experiments` | Scenario catalog, descriptions, and lessons. |
| POST | `/api/runs` | Atomically create a run; requires `Idempotency-Key`. |
| GET | `/api/runs` | Latest 50 run summaries. |
| GET | `/api/runs/{id}` | Run steps, deliveries, receiver evidence, and circuit history. |
| POST | `/api/runs/{id}/resume` | Resume only this run. |
| POST | `/api/runs/{id}/recover` | Restore this run's receivers and replay its exhausted deliveries. |

Create a run with `{ "scenario": "fail-twice" }`. Available scenarios are `reject`, `lost-ack`, `pause`, `fail-twice`, `rate-limit`, `permanent`, `burst`, `circuit-breaker`, `out-of-order`, and `two-partners`. Only `burst` accepts an optional `batchSize` from 1–50. Repeating a matching request/key returns the same run and delivery IDs; conflicting content returns 409. The response is `{ id, reused, conflict, deliveryIds }`.

Run and event links can be combined as `?run=<run UUID>&event=<delivery UUID>`. Recovery is idempotent but spans receiver HTTP calls; if one receiver is unreachable, it returns 503 and can safely be retried. Existing scheduled retries keep their due time. The global worker pause must also be off for resumed runs to deliver.

Guided events add signed `runId` and `scenario` metadata and `data.version`. Version 1 is `order.confirmed` with `status: confirmed`; version 2 is `order.cancelled` with `status: cancelled`. Optional fields default compatibly for first-release messages. Receiver success can include `stale: true` when an older version was durably acknowledged without applying it.

The internal partner also provides authenticated `GET /api/events/{eventId}`, `GET /api/runs/{runId}`, and `POST /api/runs/{runId}` with `{ "mode": "healthy" }` or `reject`. They require the existing control key.

### Create an order

```sh
curl -i http://localhost:5188/api/orders \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: example-order-0001' \
  -d '{"reference":"DEMO-0001","sku":"LAMP-01","quantity":1}'
```

On Windows PowerShell, use `curl.exe` or `Invoke-RestMethod`:

```powershell
Invoke-RestMethod http://localhost:5188/api/orders -Method Post `
  -ContentType 'application/json' `
  -Headers @{ 'Idempotency-Key' = 'example-order-0001' } `
  -Body '{"reference":"DEMO-0001","sku":"LAMP-01","quantity":1}'
```

The first request returns **201**, with `orderId`, `eventId`, and `reused: false`. Repeat the same request and key to receive **200** with the original IDs and `reused: true`. A key reused for different normalized content returns **409**. The key is 16–128 ASCII letters, digits, hyphens, underscores, dots, or colons.

The reference is trimmed and must contain 1–80 characters. The three SKUs are `LAMP-01`, `DESK-02`, and `KIT-03`. Quantity is an integer from 1 to 100. There are no real purchases, customer records, or external fulfillment calls.

Replay keeps the event ID and JSON. Replaying Pending, Retrying, Delivering, or an unknown ID returns **409**. Valid replay returns **202**.

Modes are `healthy`, `reject`, `slow`, `offline`, and `lost-ack`. Delay is 3000–15000 milliseconds; the dashboard uses 5000. Control calls return **503** if the partner process is unavailable. Simulated failures apply only to its webhook endpoint, so its control API remains usable.

## Webhook contract

The partner is internal to Compose. The lab sends `POST http://partner:8080/webhooks/orders` with these headers:

```text
Content-Type: application/json; charset=utf-8
Idempotency-Key: <event UUID>
X-Octo-Event-Id: <event UUID>
X-Octo-Attempt: <total attempt number>
X-Octo-Timestamp: <Unix seconds>
X-Octo-Signature: v1=<lowercase SHA256 HMAC hex>
```

Example body:

```json
{
    "id": "9a066ee3-4772-4f04-a340-7ed861971d42",
    "type": "order.confirmed",
    "occurredAt": "2026-10-10T12:00:00+00:00",
    "data": {
        "id": "c38db162-e25e-4128-8a50-786e6400a5c7",
        "reference": "DEMO-0001",
        "sku": "LAMP-01",
        "quantity": 1,
        "status": "confirmed"
    }
}
```

To sign, concatenate the timestamp, a dot, the event ID, a dot, and the **unchanged serialized body**. Encode that string and the shared secret as UTF-8. Compute HMAC-SHA256 and prefix its lowercase hexadecimal encoding with `v1=`. The sample is pretty-printed for readability; verification uses the actual request's body, without reformatting it.

The partner verifies the signature in constant time, accepts a maximum timestamp skew of five minutes, and rejects an ID/header mismatch. Requests are limited to 16 KiB. Attempt number is diagnostic metadata, not part of the signature. A retry signs the same body and event ID with a fresh timestamp. The receiver deduplicates using the signed `X-Octo-Event-Id`, not the diagnostic `Idempotency-Key` header.

Successful responses contain `{ "eventId": "...", "duplicate": false }`, or `true` for a matching receipt. Invalid signatures return **401**; malformed events return **400**; valid but conflicting event/order identifiers return **409**. HTTP 2xx itself is the sender's acknowledgement; receipt JSON is optional diagnostic information.

`GET/POST /api/control` on the partner requires `X-Lab-Control-Key`. This key is independent of the signing secret. The lab proxies these calls; the browser never receives either secret.
