export async function api(path, body, headers = {}) {
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: { ...(body === undefined ? {} : { "Content-Type": "application/json" }), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(8000),
  });
  const text = await response.text();
  let result;
  try { result = text ? JSON.parse(text) : null; } catch { result = null; }
  if (!response.ok) throw new Error(result?.error || result?.title || `Request failed (HTTP ${response.status}).`);
  return result;
}
