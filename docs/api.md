# HTTP API

The lab listens at `http://localhost:5188`. Use `Content-Type: application/json` for every POST, including an empty `{}` replay body. Browser writes must come from the lab's own origin. The API is intentionally unauthenticated and bound to localhost by Compose.

## Lab endpoints

| Method | Path                               | Purpose                                                         |
| ------ | ---------------------------------- | --------------------------------------------------------------- |
| GET    | `/health`                          | Check the host and its database connection.                     |
| GET    | `/api/config`                      | Retry, timeout, and lease settings; no keys.                    |
| GET    | `/api/state`                       | Global counts, persisted pause state, filtered delivery page. |
| POST   | `/api/orders`                      | Create an order and its outbox event atomically.                |
| GET    | `/api/deliveries/{eventId}`        | Immutable event payload and ordered attempt history.            |
| POST   | `/api/deliveries/{eventId}/replay` | Requeue a delivered or exhausted event.                         |
| POST   | `/api/worker`                      | Persist `{ "paused": true }` or `{ "paused": false }`.          |
| GET    | `/api/partner`                     | Read the partner's controls, counters, and latest 100 receipts. |
| POST   | `/api/partner`                     | Set `{ "mode": "healthy", "delayMs": 5000 }`.                   |

### Create an order

`GET /api/state` accepts `search` (reference, delivery UUID, or SKU), `status`, `page` (one-based), and `pageSize` (1–100). Defaults remain the newest 100 deliveries. `matched`, `page`, and `pageSize` describe the filtered result; the top-level counts remain global. The dashboard uses 20 rows per page.

`GET /api/deliveries/{eventId}` also returns a self-contained `delivery` summary, `rawPayload`, and `payloadHash` (SHA-256 of the UTF-8 body). `payload` is the parsed JSON for readable display. Only `rawPayload` represents the signed bytes. An event remains inspectable when it is outside the current page or filters. Dashboard links use `?event=<UUID>`.

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
