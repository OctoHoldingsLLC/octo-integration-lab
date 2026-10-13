export const $ = (id) => document.getElementById(id);
export const escape = (value) =>
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
      <td><strong>${escape(event.reference)}</strong><small>${escape(event.eventId.slice(0, 8))} · ${event.destination === "primary" ? "Partner A" : "Partner B"} · v${event.version}</small></td>
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
    `EVENT ${event.eventId} · ${event.destination === "primary" ? "PARTNER A" : "PARTNER B"} · v${event.version} · ${event.status.toUpperCase()}`;
  $("replay-event").disabled =
    busy || !["Delivered", "Exhausted"].includes(event.status);
  $("replay-event").title = ["Delivered", "Exhausted"].includes(event.status)
    ? "Requeue the same event, preserve history, and start a fresh retry budget."
    : "Wait for this delivery to finish before replaying.";
  const version = JSON.stringify([detail, event.status, $("payload-format").value]);
  if (detailVersion === version) return;
  detailVersion = version;
  const traces = [...new Set(detail.attempts.map(attempt => attempt.traceId).filter(Boolean))];
  $("trace-evidence").textContent = traces.length ? `TRACE ${traces.join(" · ")} · Search this ID in the optional local trace viewer.` : "Trace correlation begins with the first delivery attempt.";
  const entries = [{ at: detail.payload.occurredAt, label: "Sender · Order and event committed",
    text: `One transaction saved the order and its ${detail.payload.type} event.`, meta: "", side: "sender" }];
  for (const attempt of detail.attempts) {
    entries.push({ at: attempt.startedAt, label: `Sender · Attempt ${attempt.number} claimed`,
      text: "The worker claimed this delivery and started preparing its signed request.", meta: "", side: "sender" });
    if (attempt.completedAt) entries.push({ at: attempt.completedAt, label: `Sender · Attempt ${attempt.number} · ${attempt.outcome}`,
      text: attempt.detail, meta: `${attempt.durationMs === null ? "" : ` · ${attempt.durationMs}ms`}${attempt.httpStatus === null ? "" : ` · HTTP ${attempt.httpStatus}`}`, side: "sender" });
  }
  for (const observation of detail.receiver?.observations || []) entries.push({
    at: observation.at, label: `Receiver · ${observation.outcome}`, text: observation.detail,
    meta: observation.httpStatus === null ? "" : ` · HTTP ${observation.httpStatus}`, side: "receiver",
  });
  entries.sort((a, b) => new Date(a.at) - new Date(b.at));
  const history = entries.map(entry => `<li><div class="${entry.side}-entry"><strong>${escape(entry.label)}</strong>
    <p>${escape(entry.text)}</p><div class="attempt-meta">${escape(time(entry.at))}${escape(entry.meta)}</div></div></li>`).join("");
  $("receiver-evidence").textContent = detail.receiverAvailable
    ? "Sender and receiver observations are read independently and ordered by their clocks."
    : "Receiver evidence is unavailable. A missing acknowledgement does not prove that the order failed.";
  const waiting =
    event.status === "Pending" || event.status === "Retrying"
      ? `<li><div><strong>${event.status === "Pending" ? "Queued for delivery" : "Retry scheduled"}</strong><p>The event stays in PostgreSQL until the worker can claim it.${event.status === "Retrying" ? ` Next attempt due ${escape(time(event.nextAttemptAt))}.` : ""}</p></div></li>`
      : "";
  $("timeline").innerHTML = history + waiting;
  $("event-payload").textContent = $("payload-format").value === "raw"
    ? detail.rawPayload : JSON.stringify(detail.payload, null, 2);
  $("payload-hash").textContent = `SHA-256 · ${detail.payloadHash}`;
}
