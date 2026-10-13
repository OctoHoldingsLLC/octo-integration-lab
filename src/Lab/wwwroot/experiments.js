import { api } from "./api.js";
import { $, escape, announce } from "./view.js";
import { readSession, saveSession } from "./session.js";

let runId = new URL(location.href).searchParams.get("run");
let current = null;
let catalogLoaded = false;
let busy = false;
let version = "";
let callbacks;
export const selectedRun = () => runId;

function chooseRun(id) {
  runId = id || null;
  current = null;
  const url = new URL(location.href);
  if (runId) url.searchParams.set("run", runId);
  else url.searchParams.delete("run");
  history.replaceState(null, "", url);
  callbacks.filterChanged();
}

export function setExperimentBusy(value) {
  busy = value;
  document.querySelectorAll("[data-scenario]").forEach(button => button.disabled = busy);
  $("resume-run").disabled = busy;
  const observedFailure = current?.run.scenario === "reject"
    ? current.steps[1].complete
    : current?.circuits.some(circuit => circuit.history.some(entry => entry.state === "Open"));
  $("recover-run").disabled = busy || !observedFailure;
  $("recover-run").title = observedFailure ? "Restore this run's receiver" : "Wait for the failure evidence before restoring the receiver.";
}

export function initExperiments(handlers) {
  callbacks = handlers;
  $("experiment-catalog").addEventListener("click", event => {
    const button = event.target.closest("[data-scenario]");
    if (!button) return;
    callbacks.act(async () => {
      const scenario = button.dataset.scenario;
      const request = { scenario };
      if (scenario === "burst") {
        if (!$("batch-size").reportValidity()) return;
        request.batchSize = Number($("batch-size").value);
      }
      let pending = readSession().pendingExperiment;
      if (!pending || JSON.stringify(pending.request) !== JSON.stringify(request))
        pending = { request, key: crypto.randomUUID() };
      saveSession({ pendingExperiment: pending });
      const result = await api("/api/runs", request, { "Idempotency-Key": pending.key });
      saveSession({ pendingExperiment: null });
      chooseRun(result.id);
      callbacks.selectEvent(result.deliveryIds[0]);
      announce(result.reused ? "Restored the same experiment run." : "Experiment saved. Follow the evidence in the guided steps.");
      await refreshExperiments();
      $("run-panel").scrollIntoView({ behavior: "instant", block: "start" });
    });
  });
  $("filter-run").addEventListener("change", () => chooseRun($("filter-run").value));
  $("clear-run").addEventListener("click", () => chooseRun(null));
  $("resume-run").addEventListener("click", () => callbacks.act(async () => {
    await api(`/api/runs/${runId}/resume`, {});
    announce("This run resumed. Its original orders and event IDs are preserved.");
  }));
  $("recover-run").addEventListener("click", () => callbacks.act(async () => {
    await api(`/api/runs/${runId}/recover`, {});
    announce("This run's receivers are healthy. Exhausted deliveries were replayed; scheduled retries keep their due times.");
  }));
}

export async function refreshExperiments() {
  if (!catalogLoaded) {
    const catalog = await api("/api/experiments");
    $("experiment-catalog").innerHTML = catalog.map((item, i) => `
      <button class="experiment" type="button" data-scenario="${escape(item.id)}">
        <span class="experiment-number">${String(i + 1).padStart(2, "0")} / ${escape(item.id.replaceAll("-", " ").toUpperCase())}</span>
        <strong>${escape(item.title)}</strong><span>${escape(item.summary)}</span>
        <span class="experiment-link">Start experiment ↗</span>
      </button>`).join("");
    catalogLoaded = true;
  }
  const selected = runId;
  const [runs, run] = await Promise.all([api("/api/runs"), selected ? api(`/api/runs/${selected}`) : null]);
  if (selected !== runId) return;
  current = run;
  const options = [{ id: "", label: "All runs and manual orders" }, ...runs.map(item => ({
    id: item.id, label: `${item.scenario} · ${item.id.slice(0, 8)}`,
  }))];
  if (run && !runs.some(item => item.id === runId)) options.push({ id: runId, label: `${run.run.scenario} · ${runId.slice(0, 8)}` });
  const markup = options.map(item => `<option value="${escape(item.id)}">${escape(item.label)}</option>`).join("");
  if (version !== markup) { $("filter-run").innerHTML = markup; version = markup; }
  $("filter-run").value = runId || "";
  $("run-panel").hidden = !run;
  if (!run) { setExperimentBusy(busy); return; }
  $("run-title").textContent = run.definition.title;
  $("run-status").textContent = run.complete ? "Experiment complete" : run.globalPaused ? "Global worker paused" : run.run.paused ? "Run paused" : "Following the evidence…";
  $("run-status").classList.toggle("complete", run.complete);
  $("run-lesson").textContent = run.definition.lesson;
  $("run-steps").innerHTML = run.steps.map(step => `<li class="${step.complete ? "complete" : "waiting"}">
    <span aria-hidden="true">${step.complete ? "✓" : "○"}</span> ${escape(step.label)}
    <span class="sr-only">${step.complete ? "Complete" : "Waiting"}</span></li>`).join("");
  $("run-scope").textContent = `Run ${run.run.id.slice(0, 8)} · ${run.run.batchSize} order${run.run.batchSize === 1 ? "" : "s"} · Controls below affect only this run. Global pause still applies to every run.`;
  $("resume-run").hidden = !run.run.paused;
  $("recover-run").hidden = !["reject", "circuit-breaker"].includes(run.run.scenario) || run.complete;
  $("run-destinations").innerHTML = Object.entries(run.receivers).sort(([a], [b]) => a.localeCompare(b)).map(([name, events]) => {
    const deliveries = run.deliveries.filter(d => d.destination === name);
    const attempts = deliveries.reduce((sum, d) => sum + d.attempts, 0);
    const receipts = events?.filter(e => e.received).length;
    return `<div><strong>${escape(name === "primary" ? "Partner A" : "Partner B")}</strong>
      <span>${deliveries.filter(d => d.status === "Delivered").length}/${deliveries.length} delivered · ${attempts} attempt${attempts === 1 ? "" : "s"}</span>
      <span>${events === null ? "Receiver evidence unavailable" : `${receipts} durable receipt${receipts === 1 ? "" : "s"}`}</span></div>`;
  }).join("");
  $("run-circuits").innerHTML = run.circuits.map(circuit => `<div class="circuit-state"><strong>Circuit: ${escape(circuit.state)}</strong>
    <p>${circuit.state === "Open" ? "New sends are on hold. One probe will test recovery when the wait ends." : circuit.state === "HalfOpen" ? "A single recovery probe is in flight." : "The circuit allows delivery."}</p>
    <ol>${circuit.history.map(entry => `<li>${escape(new Date(entry.at).toLocaleTimeString())} · ${escape(entry.state)} — ${escape(entry.reason)}</li>`).join("")}</ol></div>`).join("");
  setExperimentBusy(busy);
}
