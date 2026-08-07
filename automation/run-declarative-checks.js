#!/usr/bin/env node
// Runs every "Automation-ready" test case that has an ApiEndpoint configured (Test Case ->
// Edit -> Automation Config in the UI) WITHOUT needing a hand-written script for each one --
// the test case's own stored metadata (endpoint, method, expected status) is enough to build
// and run the check, and the result is reported the same way a Playwright test would be.
//
// DB-query checks (DbQuery/DbExpectedValue) are intentionally NOT run here: TestCaseHub
// encrypts environment DB connection strings at rest and never returns them via the API (by
// design -- see EnvironmentsController), so a DB check needs its OWN separate connection
// string supplied as a CI secret (TCH_DB_CONNECTION_STRING), matching whatever the Admin
// entered for that Environment Target. See README "DB checks" for how to wire that up.
//
// Usage: node automation/run-declarative-checks.js
// Required env: TCH_CI_BOT_EMAIL, TCH_CI_BOT_PASSWORD, TCH_API_KEY, TCH_RUN_ID,
//               TCH_APP_API_BASE_URL (the Environment Target's appApiBaseUrl)

const { login, listTestCases, reportAutomatedResult } = require('./lib/testcasehub-client');

async function main() {
  const runId = requireEnv('TCH_RUN_ID');
  const apiKey = requireEnv('TCH_API_KEY');
  const appApiBaseUrl = requireEnv('TCH_APP_API_BASE_URL');
  const botEmail = requireEnv('TCH_CI_BOT_EMAIL');
  const botPassword = requireEnv('TCH_CI_BOT_PASSWORD');

  const token = await login(botEmail, botPassword);
  const testCases = await listTestCases(token, {}); // add moduleId/layer filters here if you only want a subset

  const withApiConfig = testCases.filter(tc =>
    tc.automationReady && tc.automationConfig && tc.automationConfig.apiEndpoint
  );

  console.log(`Found ${withApiConfig.length} automation-ready test case(s) with an API check configured.`);

  let failed = 0;
  for (const tc of withApiConfig) {
    const { apiEndpoint, apiMethod, apiExpectedStatus } = tc.automationConfig;
    const method = apiMethod || 'GET';
    const url = `${appApiBaseUrl}${apiEndpoint}`;
    let status = 'Pass';
    let notes = '';

    try {
      const res = await fetch(url, { method });
      if (apiExpectedStatus && res.status !== apiExpectedStatus) {
        status = 'Fail';
        notes = `Expected HTTP ${apiExpectedStatus}, got ${res.status}`;
      } else {
        notes = `HTTP ${res.status}`;
      }
    } catch (err) {
      status = 'Fail';
      notes = `Request failed: ${err.message}`;
    }

    if (status === 'Fail') failed++;
    console.log(`  [${status}] ${tc.id} -- ${method} ${apiEndpoint} (${notes})`);

    await reportAutomatedResult(apiKey, runId, {
      testCaseId: tc.id,
      platform: 'API',
      status,
      notes,
      runAttemptKey: `${runId}:${tc.id}:declarative:1`,
      retryCount: 0
    }, true);
  }

  console.log(`\nDeclarative API checks done: ${withApiConfig.length - failed} passed, ${failed} failed.`);
  if (failed > 0) process.exitCode = 1;
}

function requireEnv(name) {
  const v = process.env[name];
  if (!v) { console.error(`Missing required env var: ${name}`); process.exit(1); }
  return v;
}

main().catch(err => { console.error(err); process.exit(1); });
