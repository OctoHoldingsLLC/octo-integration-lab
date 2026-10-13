import {
  $,
  announce,
  connectionError,
  renderState,
  renderPartner,
  renderDetail,
} from "./view.js";
import { readSession, saveSession } from "./session.js";
import { api } from "./api.js";
import { renderMetrics } from "./metrics.js";
import { initExperiments, refreshExperiments, selectedRun, setExperimentBusy } from "./experiments.js";

let state = null;
let partner = null;
let selected = new URL(location.href).searchParams.get("event");
let busy = false;
let refreshing = false;
const session = readSession();
let lastRequest = session.lastRequest || null;
let pendingRequest = session.pendingRequest || null;
let referenceNumber = session.referenceNumber || 1001;
let page = 1;
let configLoaded = false;

function persist() {
  saveSession({ lastRequest, pendingRequest, referenceNumber });
}

function selectEvent(id) {
  selected = id;
  const url = new URL(location.href);
  if (id) url.searchParams.set("event", id);
  else url.searchParams.delete("event");
  history.replaceState(null, "", url);
}

if (pendingRequest) {
  $("reference").value = pendingRequest.order.reference;
  $("sku").value = pendingRequest.order.sku;
  $("quantity").value = pendingRequest.order.quantity;
  announce("An earlier order has an unconfirmed response. Submit it again to safely reuse its request key.");
} else $("reference").value = `DEMO-${referenceNumber}`;

function render() {
  if (state) renderState(state, selected, busy);
  renderPartner(partner, busy);
  $("repeat-order").disabled = busy || !lastRequest;
  setExperimentBusy(busy);
}

async function loadDetail() {
  const id = selected;
  if (!id) return;
  const detail = await api(`/api/deliveries/${id}`);
  if (selected === id) renderDetail(detail, detail.delivery, busy);
}

async function refresh() {
  if (refreshing || document.hidden) return;
  refreshing = true;
  try {
    const run = selectedRun();
    const results = await Promise.allSettled([
      api(`/api/state?${new URLSearchParams({ search: $("search-events").value, status: $("filter-status").value, page, pageSize: 20, ...(selectedRun() ? { runId: selectedRun() } : {}) })}`),
      api("/api/partner"),
      api(`/api/metrics${run ? `?runId=${run}` : ""}`),
    ]);
    if (results[0].status === "rejected") throw results[0].reason;
    state = results[0].value;
    partner = results[1].status === "fulfilled" ? results[1].value : null;
    if (results[2].status === "rejected") throw results[2].reason;
    if (run === selectedRun()) renderMetrics(results[2].value);
    connectionError(
      partner
        ? ""
        : "The partner service is unreachable. Orders can still be queued; restore the service to deliver them.",
    );
    render();
    await loadDetail();
    await refreshExperiments();
    if (!configLoaded) await loadConfig();
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
  persist();
  const result = await api("/api/orders", request.order, {
    "Idempotency-Key": request.key,
  });
  lastRequest = request;
  pendingRequest = null;
  selectEvent(result.eventId);
  announce(
    result.reused
      ? "The same request returned the original order. No additional event was created."
      : "Order saved. Its event is safely queued for delivery.",
  );
  $("reference").value = `DEMO-${++referenceNumber}`;
  persist();
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
    selectEvent(result.eventId);
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
  selectEvent(button.dataset.event);
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

async function loadConfig() {
  try {
    const config = await api("/api/config");
    $("delivery-policy").textContent =
      `${config.MaxAttempts ?? config.maxAttempts} attempts per cycle · ${config.TimeoutSeconds ?? config.timeoutSeconds}s timeout · exponential backoff + jitter. Replay starts a new cycle.`;
    configLoaded = true;
  } catch {
    $("delivery-policy").textContent =
      "Delivery settings will be available when the lab reconnects.";
  }
}

$("event-search").addEventListener("submit", (event) => {
  event.preventDefault();
  page = 1;
  refresh();
});
$("filter-status").addEventListener("change", () => { page = 1; refresh(); });
$("previous-page").addEventListener("click", () => { page = Math.max(1, page - 1); refresh(); });
$("next-page").addEventListener("click", () => { page += 1; refresh(); });
$("payload-format").addEventListener("change", loadDetail);
$("copy-payload").addEventListener("click", () => act(async () => {
  await navigator.clipboard.writeText($("event-payload").textContent);
  announce("Payload copied.");
}));
$("copy-event-link").addEventListener("click", () => act(async () => {
  await navigator.clipboard.writeText(location.href);
  announce("Event link copied.");
}));

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refresh();
});
initExperiments({ act, selectEvent, filterChanged: () => {
  page = 1;
  selectEvent(null);
  $("event-detail").hidden = true;
  refresh();
} });
refresh();
setInterval(refresh, 1000);
