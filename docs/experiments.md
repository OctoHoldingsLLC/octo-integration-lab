# Follow a failure through the system

Start with `docker compose up -d --build --wait` and open http://localhost:5188. The dashboard starts empty on a new volume. Each guided experiment creates one fictional order and selects its event.

## 1. Fail, then recover

1. Start **Let it fail. Then fix it.**
2. Open the new event's timeline if it is not already shown.
3. Observe an attempt with HTTP 503 and state **Retrying**.
4. Select **Healthy** before the fourth attempt finishes.
5. The next scheduled attempt is accepted. Compare its event ID and payload with the first attempt: neither changed.

Changing the failure mode does not reset a retry's due time. Four failed attempts move the event to **Exhausted**. If that happens, select Healthy and choose **Replay event**. The timeline keeps the four failures and appends a fifth attempt.

## 2. The partner succeeded, but the sender cannot know

1. Start **The reply never arrived.**
2. The partner verifies and commits the order immediately, then waits five seconds before acknowledging it.
3. The sender's two-second timeout expires. The attempt is labeled **Timeout**, not proof of receiver failure.
4. Its retry arrives with the same signed event ID. The receiver finds its existing receipt and returns success with `duplicate: true` immediately.
5. The timeline shows **Duplicate**. The processed counter increased by one; the duplicates counter also increased.
6. Choose **Replay event** to repeat the delivery deliberately. The processed counter still does not increase for this order.

This is why retries require an idempotent receiver. A unique receipt without an atomic business transaction would still leave a crash gap.

## 3. A pending event outlives the process

1. Start **Put delivery on hold.** The worker pauses before the order is created.
2. In a terminal, run `docker compose restart lab`.
3. Refresh the dashboard. Its event is still Pending, and the worker remains paused.
4. Resume the worker. The event is delivered without creating a new order.

For a more demanding crash window, run `pwsh -File scripts/verify-recovery.ps1`. It kills the sender with SIGKILL after the partner has committed the order but before the acknowledgement. After restart, the lease expires; the original attempt becomes Interrupted, and the next attempt is acknowledged as a duplicate. Recovery may take about fifteen seconds because the lease deliberately outlasts the network timeout.

## 4. One API request, many callers

Create an order and use **Repeat last request**. This is API-level idempotency: a repeated request must not create an extra order or outbox row. It differs from receiver-level deduplication, which protects the business effect when a single event is delivered multiple times.

The integration suite sends eight concurrent requests using the same API key and eight concurrent signed webhook requests using the same event ID. The database uniqueness and transaction boundaries are exercised under real contention.

## Read the counters honestly

The sender's Delivered count and receiver's Orders processed count can differ during failures. Signed requests sent directly by tests also appear at the receiver without an API-created order in the sender. The counters are observations from separate systems, not a financial reconciliation or a distributed snapshot.

To return to a blank lab, `docker compose down --volumes` deletes this lab's database volume. Restart with `docker compose up -d --build --wait`. Ordinary `docker compose down` retains it.
