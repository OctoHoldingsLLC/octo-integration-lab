export const $ = (id) => document.getElementById(id);
const escape = (value) =>
  String(value).replace(
    /[&<>"']/g,
    (char) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        char
      ],
  );
const time = (value) =>
  new Date(value).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
let tableVersion = "";
let detailVersion = "";

export function announce(message, error = false) {
  $("notice").textContent = message;
  $("notice").classList.toggle("error", error);
  $("notice").hidden = false;
}

export function connectionError(message) {
  $("connection-error").hidden = !message;
  $("connection-error").textContent = message;
  $("sync-label").textContent = message
    ? "Reconnecting…"
    : "● Live · 1s refresh";
}

export function renderState(state, selected, busy) {
  for (const metric of ["total", "delivered", "queued", "exhausted"])
    $(metric).textContent = state[metric];
  $("worker-label").textContent = state.paused
    ? "Paused · in-flight work may finish"
    : "Running · waiting for events";
  $("pause-worker").textContent = state.paused ? "Resume" : "Pause";
  $("pause-worker").disabled = busy;
  $("send-order").disabled = busy;
  document.querySelectorAll("[data-experiment]").forEach((button) => {
    button.disabled = busy;
  });
  $("empty").hidden = state.deliveries.length > 0;
  $("delivery-table").hidden = state.deliveries.length === 0;
  $("page-status").textContent = `Page ${state.page} of ${Math.max(1, Math.ceil(state.matched / state.pageSize))} · ${state.matched} matching deliveries`;
  $("previous-page").disabled = state.page <= 1;
  $("next-page").disabled = state.page * state.pageSize >= state.matched;
  const version = JSON.stringify([state.deliveries, selected]);
  if (tableVersion === version) return;
  tableVersion = version;
  const focused = document.activeElement?.dataset.event;
  $("deliveries").innerHTML = state.deliveries
    .map(
      (event) => `
    <tr class="${event.id === selected ? "selected" : ""}">
      <td><strong>${escape(event.reference)}</strong><small>${escape(event.id.slice(0, 8))} · ${escape(event.sku)} × ${event.quantity}</small></td>
      <td><span class="status ${escape(event.status)}">${escape(event.status)}</span></td>
      <td>${event.attempts}${event.replays ? ` <span title="Replay cycles">/ ${event.replays} replay${event.replays === 1 ? "" : "s"}</span>` : ""}</td>
      <td>${escape(time(event.updatedAt))}</td>
      <td><button type="button" class="inspect" data-event="${escape(event.id)}" aria-label="Inspect ${escape(event.reference)}" aria-pressed="${event.id === selected}">↗</button></td>
    </tr>`,
    )
    .join("");
  if (focused)
    document
      .querySelector(`[data-event="${CSS.escape(focused)}"]`)
      ?.focus({ preventScroll: true });
}

export function renderPartner(partner, busy) {
  $("partner-controls").disabled = busy || !partner;
  $("processed").textContent = partner?.processed ?? "—";
  $("duplicates").textContent = partner?.duplicates ?? "—";
  if (!partner || busy) return;
  document.querySelectorAll("input[name=mode]").forEach((radio) => {
    radio.checked = radio.value === partner.mode;
  });
}

export function renderDetail(detail, event, busy) {
  $("event-detail").hidden = false;
  $("detail-title").textContent = event.reference;
  $("event-meta").textContent =
    `EVENT ${detail.id} · ${event.status.toUpperCase()}`;
  $("replay-event").disabled =
    busy || !["Delivered", "Exhausted"].includes(event.status);
  $("replay-event").title = ["Delivered", "Exhausted"].includes(event.status)
    ? "Requeue the same event, preserve history, and start a fresh retry budget."
    : "Wait for this delivery to finish before replaying.";
  const version = JSON.stringify([detail, event.status, $("payload-format").value]);
  if (detailVersion === version) return;
  detailVersion = version;
  const start = `<li><div><strong>Order and event committed</strong><p>One transaction saved the order and its outbox event.</p><div class="attempt-meta">${escape(time(detail.payload.occurredAt))} · order.confirmed</div></div></li>`;
  const attempts = detail.attempts
    .map(
      (attempt) => `<li><div>
    <strong>Attempt ${attempt.number} · ${escape(attempt.outcome)}</strong>
    <p>${escape(attempt.detail || "A signed request is on its way to the partner.")}</p>
    <div class="attempt-meta">${escape(time(attempt.startedAt))}${attempt.durationMs !== null ? ` · ${attempt.durationMs}ms` : ""}${attempt.httpStatus !== null ? ` · HTTP ${attempt.httpStatus}` : ""}</div>
  </div></li>`,
    )
    .join("");
  const waiting =
    event.status === "Pending" || event.status === "Retrying"
      ? `<li><div><strong>${event.status === "Pending" ? "Queued for delivery" : "Retry scheduled"}</strong><p>The event stays in PostgreSQL until the worker can claim it.${event.status === "Retrying" ? ` Next attempt due ${escape(time(event.nextAttemptAt))}.` : ""}</p></div></li>`
      : "";
  $("timeline").innerHTML = start + attempts + waiting;
  $("event-payload").textContent = $("payload-format").value === "raw"
    ? detail.rawPayload : JSON.stringify(detail.payload, null, 2);
  $("payload-hash").textContent = `SHA-256 · ${detail.payloadHash}`;
}
