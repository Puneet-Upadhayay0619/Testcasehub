// Custom Playwright reporter: automatically posts EVERY test's result to TestCaseHub, with
// no per-spec code required. This is what makes step "run scripts + report results" fully
// automatic instead of something a human (or each spec file) has to remember to call.
//
// Convention this relies on: give each Playwright test a title that starts with the
// TestCaseHub test-case ID in brackets, e.g.:
//   test('[TC-RETAIL-DSH-001] shows the dashboard after login', async ({ page }) => { ... });
// Tests without a recognizable [TC-...] prefix are run normally but simply not reported
// (so you can mix TestCaseHub-tracked tests with ad-hoc ones in the same suite).
//
// Wire it up in playwright.config.js:
//   reporter: [['./automation/lib/playwright-reporter.js', { runId: process.env.TCH_RUN_ID }]]

const { reportAutomatedResult } = require('./testcasehub-client');

const TC_ID_PATTERN = /\[(TC-[A-Za-z0-9-]+)\]/;
const STATUS_MAP = { passed: 'Pass', failed: 'Fail', timedOut: 'Fail', skipped: 'Skipped', interrupted: 'Blocked' };

class TestCaseHubReporter {
  constructor(options = {}) {
    this.runId = options.runId || process.env.TCH_RUN_ID;
    this.apiKey = options.apiKey || process.env.TCH_API_KEY;
    this.retryCount = 0;
    if (!this.runId) console.warn('[testcasehub-reporter] TCH_RUN_ID not set -- results will NOT be reported.');
    if (!this.apiKey) console.warn('[testcasehub-reporter] TCH_API_KEY not set -- results will NOT be reported.');
  }

  onTestEnd(test, result) {
    if (!this.runId || !this.apiKey) return;

    const match = test.title.match(TC_ID_PATTERN);
    if (!match) return; // not a TestCaseHub-tracked test -- silently skip

    const testCaseId = match[1];
    const status = STATUS_MAP[result.status] || 'NotRun';
    const platform = process.env.TCH_PLATFORM || 'Web';
    // One idempotency key per actual attempt: same test + same retry number posted twice
    // (e.g. a retried CI job step) resolves to the same already-recorded result instead of
    // creating a duplicate row.
    const runAttemptKey = `${this.runId}:${testCaseId}:${result.retry}:${process.env.GITHUB_RUN_ATTEMPT || process.env.BUILD_BUILDNUMBER || '1'}`;

    const notesParts = [];
    if (result.error && result.error.message) notesParts.push(result.error.message.slice(0, 500));
    for (const a of result.attachments || []) if (a.name === 'screenshot') notesParts.push(`screenshot: ${a.path}`);

    // Fire-and-forget-but-tracked: Playwright reporters can't easily await per-test hooks
    // across all versions, so we queue the promise and flush in onEnd().
    this._pending = this._pending || [];
    this._pending.push(
      reportAutomatedResult(this.apiKey, this.runId, {
        testCaseId, platform, status,
        notes: notesParts.join(' | ') || null,
        runAttemptKey,
        retryCount: result.retry
      }, true).catch(err => console.error(`[testcasehub-reporter] failed to report ${testCaseId}:`, err.message))
    );
  }

  async onEnd() {
    if (this._pending && this._pending.length) await Promise.all(this._pending);
  }
}

module.exports = TestCaseHubReporter;
