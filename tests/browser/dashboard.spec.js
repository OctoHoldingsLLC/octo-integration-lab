import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test.beforeEach(async ({ request }) => {
  expect((await request.post("/api/partner", { data: { mode: "healthy", delayMs: 5000 } })).ok()).toBeTruthy();
  expect((await request.post("/api/worker", { data: { paused: false } })).ok()).toBeTruthy();
});

test("a committed order with a lost browser response is safely retried after reload", async ({ page, request }) => {
  await page.goto("/");
  const reference = `RELOAD-${crypto.randomUUID()}`;
  await page.getByLabel("Order reference", { exact: true }).fill(reference);
  let committed;
  await page.route("**/api/orders", async (route) => {
    const response = await route.fetch();
    committed = await response.json();
    await route.abort("failed");
  }, { times: 1 });
  await page.getByRole("button", { name: "Create order" }).click();
  await expect(page.locator("#notice")).toHaveClass(/error/);
  await page.reload();
  await expect(page.getByLabel("Order reference", { exact: true })).toHaveValue(reference);
  await page.getByRole("button", { name: "Create order" }).click();
  await expect(page.locator("#notice")).toContainText("No additional event");
  expect(new URL(page.url()).searchParams.get("event")).toBe(committed.eventId);
  const state = await (await request.get(`/api/state?search=${reference}`)).json();
  expect(state.matched).toBe(1);
  await page.reload();
  await expect(page.getByRole("button", { name: /Repeat last request/ })).toBeEnabled();
});

test("deep links, search, exact payload, keyboard disclosure, and replay", async ({ page, request }) => {
  const reference = `INSPECT-${crypto.randomUUID()}`;
  const created = await (await request.post("/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { reference, sku: "KIT-03", quantity: 1 },
  })).json();
  await page.goto(`/?event=${created.eventId}`);
  await expect(page.locator("#detail-title")).toHaveText(reference);
  await page.getByLabel("Find an event").fill("no-matching-events");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page.locator("#page-status")).toContainText("0 matching");
  await expect(page.locator("#event-meta")).toContainText("DELIVERED");
  const summary = page.locator("#payload-disclosure summary");
  await summary.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#event-payload")).toBeVisible();
  await page.getByLabel("Display", { exact: true }).selectOption("raw");
  const detail = await (await request.get(`/api/deliveries/${created.eventId}`)).json();
  await expect(page.locator("#event-payload")).toHaveText(detail.rawPayload);
  await expect(page.locator("#payload-hash")).toContainText(detail.payloadHash);
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.getByRole("button", { name: "Copy payload", exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(detail.rawPayload);
  await page.getByRole("button", { name: "Copy event link", exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(page.url());
  await page.getByRole("button", { name: /Replay event/ }).click();
  await expect(page.locator("#timeline")).toContainText("Duplicate");
});

test("connection failures recover and configuration retries", async ({ page }) => {
  await page.route("**/api/config", (route) => route.abort(), { times: 1 });
  await page.route("**/api/state?**", (route) => route.abort(), { times: 1 });
  await page.goto("/");
  await expect(page.locator("#connection-error")).toBeVisible();
  await expect(page.locator("#connection-error")).toBeHidden();
  await expect(page.locator("#delivery-policy")).toContainText("attempts per cycle");
  await expect(page.getByRole("button", { name: "Create order" })).toBeEnabled();
});

test("search, status filters, and both pagination controls navigate real deliveries", async ({ page, request }) => {
  const prefix = `PAGING-${crypto.randomUUID().slice(0, 8)}`;
  await request.post("/api/worker", { data: { paused: true } });
  try {
    for (let i = 0; i < 23; i++) {
      const response = await request.post("/api/orders", {
        headers: { "Idempotency-Key": crypto.randomUUID() },
        data: { reference: `${prefix}-${i}`, sku: "KIT-03", quantity: 1 },
      });
      expect(response.ok()).toBeTruthy();
    }
    await page.goto("/");
    await page.getByLabel("Find an event").fill(prefix);
    await page.getByLabel("Status", { exact: true }).selectOption("Pending");
    await expect(page.locator("#page-status")).toContainText("Page 1 of 2 · 23 matching");
    await page.getByRole("button", { name: "Next", exact: true }).click();
    await expect(page.locator("#deliveries tr")).toHaveCount(3);
    await expect(page.locator("#page-status")).toContainText("Page 2 of 2");
    await page.getByRole("button", { name: "Previous", exact: true }).click();
    await expect(page.locator("#deliveries tr")).toHaveCount(20);
    await page.getByLabel("Status", { exact: true }).selectOption("Delivered");
    await expect(page.locator("#page-status")).toContainText("0 matching");
  } finally {
    await request.post("/api/worker", { data: { paused: false } });
  }
});

for (const width of [320, 390, 768, 1440]) {
  test(`dashboard has no horizontal overflow or detected accessibility violations at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto("/");
    await expect(page.getByRole("button", { name: "Create order" })).toBeEnabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    const report = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
    expect(report.violations).toEqual([]);
    await page.screenshot({ path: `output/playwright/dashboard-${width}.png`, fullPage: true });
  });
}
