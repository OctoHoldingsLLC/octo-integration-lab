import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test.beforeEach(async ({ request }) => {
  await request.post("/api/partner", { data: { mode: "healthy", delayMs: 5000 } });
  await request.post("/api/worker", { data: { paused: false } });
});

for (const scenario of ["reject", "lost-ack", "pause", "fail-twice", "rate-limit", "permanent", "burst", "circuit-breaker", "out-of-order", "two-partners"]) {
  test(`guided ${scenario} experiment proves its result in the dashboard`, async ({ page }) => {
    await page.goto("/");
    if (scenario === "burst") await page.getByLabel("Burst size").fill("10");
    await page.locator(`[data-scenario="${scenario}"]`).click();
    await expect(page.locator("#run-panel")).toBeVisible();
    expect(new URL(page.url()).searchParams.get("run")).toBeTruthy();
    if (scenario === "pause") {
      await expect(page.locator("#run-status")).toHaveText("Run paused");
      await page.getByRole("button", { name: "Resume this run" }).click();
    }
    if (scenario === "reject") {
      await expect(page.locator("#timeline")).toContainText("Receiver · Rejected");
      await page.getByRole("button", { name: "Restore this run's receiver", exact: true }).click();
    }
    if (scenario === "circuit-breaker") {
      await expect(page.locator("#run-circuits")).toContainText("Circuit: Open");
      await page.getByRole("button", { name: "Restore this run's receiver", exact: true }).click();
    }
    await expect(page.locator("#run-status")).toHaveText("Experiment complete", { timeout: 45000 });
    await expect(page.locator("#run-steps li.complete")).toHaveCount(3);
    await expect(page.locator("#timeline")).toContainText("Receiver ·");
    if (scenario === "lost-ack") {
      await expect(page.locator("#timeline")).toContainText("Receiver · Committed");
      await expect(page.locator("#timeline")).toContainText("Timeout");
      await expect(page.locator("#timeline")).toContainText("Duplicate");
      for (const width of [320, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
        const report = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        expect(report.violations).toEqual([]);
        await page.screenshot({ path: `output/playwright/guided-${width}.png`, fullPage: true });
      }
    }
    const url = page.url();
    await page.reload();
    await expect(page.locator("#run-status")).toHaveText("Experiment complete");
    expect(page.url()).toBe(url);
  });
}

test("invalid burst is explained without starting a run", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Burst size").fill("51");
  await page.locator('[data-scenario="burst"]').click();
  expect(await page.getByLabel("Burst size").evaluate(el => el.validity.rangeOverflow)).toBeTruthy();
  expect(new URL(page.url()).searchParams.get("run")).toBeNull();
});
