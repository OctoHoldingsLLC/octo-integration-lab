const key = "octo-lab-session-v1";

export function readSession() {
  try {
    return JSON.parse(sessionStorage.getItem(key)) || {};
  } catch {
    return {};
  }
}

export function saveSession(value) {
  // Persist before sending: after an ambiguous response, reload must reuse this key.
  sessionStorage.setItem(key, JSON.stringify(value));
}
