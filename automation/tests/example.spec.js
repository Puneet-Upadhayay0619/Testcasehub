// Sample Playwright spec. The ONLY thing that matters for automatic reporting is the
// "[TC-...]" prefix in each test title -- the reporter (lib/playwright-reporter.js) extracts
// it and posts Pass/Fail automatically, with zero extra code in this file. Delete this file
// once you have real specs; keep the naming convention.
const { test, expect } = require('@playwright/test');

test('[TC-RETAIL-DSH-001] shows the dashboard after login', async ({ page }) => {
  await page.goto('/login');
  await page.fill('#loginEmail', process.env.TCH_TEST_USER_EMAIL || '');
  await page.fill('#loginPassword', process.env.TCH_TEST_USER_PASSWORD || '');
  await page.click('#loginSubmit');
  await expect(page.locator('h1')).toContainText('Test Case Hub');
});

test('[TC-RETAIL-DSH-002] search box filters test cases by ID', async ({ page }) => {
  await page.goto('/');
  await page.fill('#searchBox', 'TC-RETAIL');
  await expect(page.locator('tr.row')).toHaveCount(0); // adjust to your real fixture data
});
