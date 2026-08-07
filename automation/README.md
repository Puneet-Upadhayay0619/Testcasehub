# TestCaseHub — full automation pipeline

Everything in this folder exists so that, after a one-time setup, **no manual step remains**:
CI creates the Test Run, runs the tests, reports every result, files bugs for failures, and
fails the build if anything Failed/Blocked — nightly and on every push.

## How it fits together

```
setup-once.js          <- run ONCE by hand (Admin) to bootstrap the CI Bot user, an API key,
                           and a Staging Environment Target
run-automation.js       <- the orchestrator CI actually calls: creates a Test Run, runs
                           Playwright, runs declarative API checks, files bugs, checks rollup
  |-- lib/testcasehub-client.js   shared REST client (login, create run, report result, ...)
  |-- lib/playwright-reporter.js  auto-posts every Playwright test's result, no per-spec code
  |-- tests/example.spec.js       sample spec using the [TC-...] title convention
  |-- run-declarative-checks.js   runs test cases that only need an API endpoint + expected
  |                                status (from Automation Config) -- no script needed at all
  |-- appium/run-mobile-checks.js scaffold for mobile checks (needs a real device/farm wired in)
azure-pipelines.yml     <- Azure DevOps Pipelines version of the trigger
../github-workflows/automation-tests.yml <- GitHub Actions version (move to .github/workflows/)
```

## One-time setup (you, by hand, once)

```bash
cd automation
npm install

TCH_BASE_URL=https://testcasehub.onrender.com \
ADMIN_EMAIL=you@company.com ADMIN_PASSWORD='your-admin-password' \
CI_BOT_EMAIL=ci-bot@company.com CI_BOT_PASSWORD="$(openssl rand -base64 24)" \
node setup-once.js
```

This prints 5 values. Save them as CI secrets (repo secrets in GitHub, or a Variable Group in
Azure DevOps called `TestCaseHub-CI`):

| Secret name            | Where it came from                                   |
|-------------------------|-------------------------------------------------------|
| `TCH_BASE_URL`          | your TestCaseHub URL                                   |
| `TCH_CI_BOT_EMAIL`      | printed by setup-once.js                                |
| `TCH_CI_BOT_PASSWORD`   | printed by setup-once.js                                |
| `TCH_API_KEY`           | printed by setup-once.js — **shown only once, save now** |
| `TCH_ENVIRONMENT_ID`    | printed by setup-once.js                                |
| `TCH_APP_API_BASE_URL`  | your staging app's API base URL                        |
| `TCH_TARGET_APP_URL`    | your staging app's web URL (Playwright `baseURL`)       |

## Wiring up the pipeline

- **GitHub Actions**: move `../github-workflows/automation-tests.yml` to
  `.github/workflows/automation-tests.yml` in your repo, add the secrets above, push.
- **Azure DevOps**: create a pipeline pointing at `azure-pipelines.yml`, link the
  `TestCaseHub-CI` variable group, run it.

Both trigger on push to `main` and nightly at 2am UTC — edit the `schedule`/`schedules` block
if you want a different cadence, or remove it to only run on push.

## Writing new automated tests

Just add a Playwright spec under `tests/`, with the TestCaseHub test-case ID in the title:

```js
test('[TC-RETAIL-DSH-003] logout button clears the session', async ({ page }) => { ... });
```

Nothing else to wire up — `lib/playwright-reporter.js` picks up every test with a `[TC-...]`
prefix automatically and reports it. Tests without that prefix still run, just aren't reported.

For simple checks that are *just* "hit this API endpoint, expect this status" — you don't
even need a spec file. Open the test case in TestCaseHub, mark it Automation-ready, and fill
in Automation Config → API Endpoint / Method / Expected Status. `run-declarative-checks.js`
picks it up on the next run with zero code.

## DB checks (the one step that can't be fully automated end-to-end)

TestCaseHub encrypts Environment Target DB connection strings at rest and the API never
returns them (by design — see `EnvironmentsController`). So a DB-based check (Automation
Config → DB Query / Expected Value) needs its own connection string supplied separately as a
CI secret (e.g. `TCH_DB_CONNECTION_STRING`), matching whatever the Admin entered in the UI.
There's a deliberate gap here: TestCaseHub's own API will never hand out that secret to a
script, even your own CI's — copy it once from wherever you provisioned the database, store it
as a CI secret, and write a small runner following the same pattern as
`run-declarative-checks.js` (fetch test cases with `automationConfig.dbQuery` set, run the
query with your DB driver of choice, compare to `dbExpectedValue`, report via
`reportAutomatedResult`).

## Bug filing

Automatic — `run-automation.js` calls `create-bug` for every Fail/Blocked result at the end of
a run. This requires `AzureDevOps:OrgUrl/Project/Pat` to be configured on the TestCaseHub
deployment itself (Render environment variables); until then it logs "not configured" and
continues without failing the pipeline.

## Why two credential types (CI Bot login + API Key)?

- The API Key only works on the one endpoint deliberately left open
  (`POST /results/automated`) — by design, it can't create Test Runs, read test cases, or file
  bugs. That's intentional: if the key leaks, the blast radius is "someone can post fake test
  results," not "someone can read/manage your whole account."
- Everything else (create Test Run, list test cases, file bug) needs a real JWT, hence the
  dedicated CI Bot user with just Contributor access — not an Admin account, and not your own.
