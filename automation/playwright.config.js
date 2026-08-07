// Minimal Playwright config wiring in the TestCaseHub reporter alongside the normal list
// reporter. TCH_RUN_ID and TCH_API_KEY are read from the environment (set by
// automation/run-automation.js before Playwright is spawned).
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ['list'],
    ['./lib/playwright-reporter.js', {}] // reads TCH_RUN_ID / TCH_API_KEY from process.env itself
  ],
  use: {
    baseURL: process.env.TCH_TARGET_APP_URL || 'https://staging-app.example.com',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure'
  }
});
