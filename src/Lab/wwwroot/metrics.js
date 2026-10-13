import { $ } from "./view.js";

export function renderMetrics(metrics) {
  const duration = value => value === null ? "—" : value < 60 ? `${value.toFixed(1)}s` : `${(value / 60).toFixed(1)}m`;
  $("metrics-scope").textContent = metrics.runId ? `Selected run · ${metrics.runId.slice(0, 8)}` : "All runs and manual orders";
  $("queue-waiting").textContent = metrics.queued;
  $("queue-active").textContent = metrics.inFlight;
  $("queue-age").textContent = duration(metrics.oldestQueuedSeconds);
  $("delivery-duration").textContent = duration(metrics.averageDeliverySeconds);
  $("delivery-p95").textContent = duration(metrics.p95DeliverySeconds);
  $("delivery-retries").textContent = metrics.currentCycleRetries;
  $("delivery-throughput").textContent = metrics.completedLastMinute;
}
