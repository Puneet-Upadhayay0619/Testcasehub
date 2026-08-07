#!/usr/bin/env node
// Single entry point both CI pipelines (Azure/GitHub) call -- keeps the YAML files thin and
// keeps "what a full automation run actually does" in one readable place:
//   1. Log in as the CI Bot (JWT) -- needed to create the Test Run and (later) file bugs.
//   2. Create a Test Run against the Staging Environment Target + a Suite.
//   3. Run Playwright (results self-report via the custom reporter, using the API key).
//   4. Run the declarative API-check runner (for test cases with no hand-written script).
//   5. Fetch the rollup; for every Fail/Blocked result, file an ADO bug automatically
//      (only if Azure DevOps is configured server-side -- otherwise this step just logs why
//      it was skipped, it does not fail the pipeline).
//   6. Print a summary and exit non-zero if anything failed, so the CI step goes red.
//
// Required env vars (see automation/README.md for where each one comes from):
//   TCH_BASE_URL, TCH_CI_BOT_EMAIL, TCH_CI_BOT_PASSWORD, TCH_API_KEY,
//   TCH_ENVIRONMENT_ID, TCH_APP_API_BASE_URL, TCH_TARGET_APP_URL
//   Optional: TCH_SUITE_ID, TCH_RELEASE_ID, TCH_RUN_NAME

const { execSync } = require('child_process');
const path = require('path');
const {
  login, createTestRun, getResults, getRollup, createBug
} = require('./lib/testcasehub-client');

async function main() {
  const botEmail = requireEnv('TCH_CI_BOT_EMAIL');
  const botPassword = requireEnv('TCH_CI_BOT_PASSWORD');
  const apiKey = requireEnv('TCH_API_KEY');
  const environmentId = requireEnv('TCH_ENVIRONMENT_ID');

  console.log('== 1. Logging in as CI Bot ==');
  const token = await login(botEmail, botPassword);

  console.log('== 2. Creating Test Run ==');
  const run = await createTestRun(token, {
    releaseId: process.env.TCH_RELEASE_ID ? Number(process.env.TCH_RELEASE_ID) : null,
    suiteId: process.env.TCH_SUITE_ID ? Number(process.env.TCH_SUITE_ID) : null,
    name: process.env.TCH_RUN_NAME || `CI run ${new Date().toISOString()}`,
    targetEnvironment: 'Staging',
    environmentTargetId: Number(environmentId)
  });
  console.log(`   Test Run #${run.id} created.`);

  const childEnv = { ...process.env, TCH_RUN_ID: String(run.id), TCH_API_KEY: apiKey };

  console.log('== 3. Running Playwright specs ==');
  runStep('npx playwright test --config=playwright.config.js', childEnv);

  console.log('== 4. Running declarative API checks ==');
  runStep('node run-declarative-checks.js', childEnv);

  console.log('== 5. Filing bugs for any Fail/Blocked result ==');
  const results = await getResults(token, run.id);
  const needsBug = results.filter(r => (r.status === 'Fail' || r.status === 'Blocked') && !r.bugWorkItemId);
  for (const r of needsBug) {
    try {
      const bug = await createBug(token, run.id, r.id);
      console.log(bug.success ? `   Filed bug ${bug.workItemId} for ${r.testCaseId}` : `   Bug not filed for ${r.testCaseId}: ${bug.error}`);
    } catch (err) {
      console.log(`   Bug filing errored for ${r.testCaseId}: ${err.message}`);
    }
  }

  console.log('== 6. Summary ==');
  const rollup = await getRollup(token, run.id);
  console.log(JSON.stringify(rollup, null, 2));
  console.log(`Readiness report: https://${new URL(require('./lib/testcasehub-client').BASE_URL).host}/ -> Test Runs -> #${run.id}`);

  if (rollup.failed > 0 || rollup.blocked > 0) {
    console.error(`FAILING PIPELINE: ${rollup.failed} failed, ${rollup.blocked} blocked.`);
    process.exit(1);
  }
}

function runStep(cmd, env) {
  try {
    execSync(cmd, { stdio: 'inherit', cwd: path.join(__dirname), env });
  } catch (err) {
    // Don't die here -- let the run continue so bugs still get filed and the rollup still
    // gets fetched/printed; the final rollup check below is what actually fails the pipeline.
    console.error(`Step failed (continuing): ${cmd}`);
  }
}

function requireEnv(name) {
  const v = process.env[name];
  if (!v) { console.error(`Missing required env var: ${name}`); process.exit(1); }
  return v;
}

main().catch(err => { console.error(err); process.exit(1); });
