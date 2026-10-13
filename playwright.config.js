import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests/browser",
  timeout: 60000,
  expect: { timeout: 20000 },
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  outputDir: "output/playwright/results",
  reporter: [["list"], ["html", { outputFolder: "output/playwright/report", open: "never" }]],
  use: {
    baseURL: process.env.LAB_BASE_URL || "http://127.0.0.1:5188",
    viewport: { width: 1440, height: 1100 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
});
