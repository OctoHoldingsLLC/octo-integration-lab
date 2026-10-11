import {
  $,
  announce,
  connectionError,
  renderState,
  renderPartner,
  renderDetail,
} from "./view.js";

let state = null;
let partner = null;
let selected = null;
let busy = false;
let refreshing = false;
let lastRequest = null;
let pendingRequest = null;
let referenceNumber = 1001;

async function api(path, body, headers = {}) {
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: {
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(8000),
  });
  const text = await response.text();
  let result;
  try {
    result = text ? JSON.parse(text) : null;
  } catch {
    result = null;
  }
  if (!response.ok)
    throw new Error(
      result?.error ||
        result?.title ||
        `Request failed (HTTP ${response.status}).`,
    );
  return result;
}

function render() {
  if (state) renderState(state, selected, busy);
  renderPartner(partner, busy);
  $("repeat-order").disabled = busy || !lastRequest;
}

async function loadDetail() {
  const id = selected;
  const event = state?.deliveries.find((item) => item.id === id);
  if (!id || !event) return;
  const detail = await api(`/api/deliveries/${id}`);
  if (selected === id) renderDetail(detail, event, busy);
}

async function refresh() {
  if (refreshing || document.hidden) return;
  refreshing = true;
  try {
    const results = await Promise.allSettled([
      api("/api/state"),
      api("/api/partner"),
    ]);
    if (results[0].status === "rejected") throw results[0].reason;
    state = results[0].value;
    partner = results[1].status === "fulfilled" ? results[1].value : null;
    connectionError(
      partner
        ? ""
        : "The partner service is unreachable. Orders can still be queued; restore the service to deliver them.",
    );
    render();
    await loadDetail();
  } catch (error) {
    connectionError(
      `The lab connection is unavailable. Reconnecting automatically. ${error.message}`,
    );
  } finally {
    refreshing = false;
  }
}

async function act(action) {
  if (busy) return;
  const focused = document.activeElement;
  busy = true;
  render();
  $("replay-event").disabled = true;
  try {
    await action();
  } catch (error) {
    announce(error.message, true);
  } finally {
    busy = false;
    render();
    if (document.activeElement === document.body && focused?.isConnected) {
      focused.focus({ preventScroll: true });
    }
    await refresh();
  }
}

async function submitOrder(order) {
  const payload = JSON.stringify(order);
  if (!pendingRequest || JSON.stringify(pendingRequest.order) !== payload) {
    pendingRequest = { order, key: crypto.randomUUID() };
  }
  const request = pendingRequest;
  const result = await api("/api/orders", request.order, {
    "Idempotency-Key": request.key,
  });
  lastRequest = request;
  pendingRequest = null;
  selected = result.eventId;
  announce(
    result.reused
      ? "The same request returned the original order. No additional event was created."
      : "Order saved. Its event is safely queued for delivery.",
  );
  $("reference").value = `DEMO-${++referenceNumber}`;
}

$("order-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  act(() =>
    submitOrder({
      reference: form.get("reference").trim(),
      sku: form.get("sku"),
      quantity: Number(form.get("quantity")),
    }),
  );
});

$("repeat-order").addEventListener("click", () =>
  act(async () => {
    const result = await api("/api/orders", lastRequest.order, {
      "Idempotency-Key": lastRequest.key,
    });
    selected = result.eventId;
    announce(
      "Same key, same order. The API returned the existing event without creating another.",
    );
  }),
);

$("partner-controls").addEventListener("change", (event) => {
  if (event.target.name !== "mode") return;
  act(async () => {
    await api("/api/partner", { mode: event.target.value, delayMs: 5000 });
    partner = await api("/api/partner");
    announce(
      `Partner behavior updated: ${event.target.closest("label").querySelector("strong").textContent}. Queued events keep their current retry schedule.`,
    );
  });
});

$("pause-worker").addEventListener("click", () =>
  act(async () => {
    const paused = !state.paused;
    await api("/api/worker", { paused });
    state.paused = paused;
    announce(
      paused
        ? "Worker paused. New events stay queued; an in-flight request can still finish."
        : "Worker resumed. Due events can be delivered now.",
    );
  }),
);

$("deliveries").addEventListener("click", async (event) => {
  const button = event.target.closest("[data-event]");
  if (!button) return;
  selected = button.dataset.event;
  render();
  try {
    await loadDetail();
  } catch (error) {
    announce(error.message, true);
  }
});

$("replay-event").addEventListener("click", () =>
  act(async () => {
    await api(`/api/deliveries/${selected}/replay`, {});
    announce(
      "Event requeued with its original ID and payload. Earlier attempts remain in its timeline.",
    );
  }),
);

document.querySelectorAll("[data-experiment]").forEach((button) => {
  button.addEventListener("click", () =>
    act(async () => {
      const experiment = button.dataset.experiment;
      await api("/api/worker", { paused: experiment === "pause" });
      await api("/api/partner", {
        mode: experiment === "pause" ? "healthy" : experiment,
        delayMs: 5000,
      });
      await submitOrder({
        reference: `DEMO-${referenceNumber}`,
        sku: "LAMP-01",
        quantity: 1,
      });
      announce(
        {
          reject:
            "Experiment started. Wait for a failed attempt, then choose Healthy. The scheduled retry will deliver the same event.",
          "lost-ack":
            "Experiment started. The partner will process the order, delay its reply, then recognize the retry as a duplicate.",
          pause:
            "Experiment started. The worker is paused and the event is stored in PostgreSQL. Resume the worker to deliver it. You can restart the lab container first to verify persistence.",
        }[experiment],
      );
    }),
  );
});

async function start() {
  try {
    const config = await api("/api/config");
    $("delivery-policy").textContent =
      `${config.MaxAttempts ?? config.maxAttempts} attempts per cycle · ${config.TimeoutSeconds ?? config.timeoutSeconds}s timeout · exponential backoff + jitter. Replay starts a new cycle.`;
  } catch {
    $("delivery-policy").textContent =
      "Delivery settings will be available when the lab reconnects.";
  }
  await refresh();
  setInterval(refresh, 1000);
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refresh();
});
start();
