#!/usr/bin/env node
// Run this ONCE, by hand, as an existing Admin -- it bootstraps everything a CI pipeline
// needs so no further manual clicking in the UI is required ever again:
//   1. Creates a single-use invite code.
//   2. Registers a dedicated "CI Bot" user with that invite (lands as Viewer by default).
//   3. Promotes the CI Bot to Contributor (enough for: create Test Run, record results,
//      file bugs -- NOT enough for Admin-only things like API Keys/Environments, which is
//      the point: the bot only gets the access it actually needs).
//   4. Creates a Staging Environment Target (edit the placeholder URLs below first).
//   5. Generates an API Key (used only for the one AllowAnonymous automated-results endpoint).
//   6. Prints every secret you need to paste into your CI provider ONE TIME.
//
// Usage:
//   TCH_BASE_URL=https://testcasehub.onrender.com \
//   ADMIN_EMAIL=you@company.com ADMIN_PASSWORD=... \
//   CI_BOT_EMAIL=ci-bot@company.com CI_BOT_PASSWORD=$(openssl rand -base64 24) \
//   node automation/setup-once.js

const {
  login, createInvite, registerWithInvite, listUsers, setUserRole,
  createEnvironment, createApiKey
} = require('./lib/testcasehub-client');

async function main() {
  const adminEmail = requireEnv('ADMIN_EMAIL');
  const adminPassword = requireEnv('ADMIN_PASSWORD');
  const botEmail = requireEnv('CI_BOT_EMAIL');
  const botPassword = requireEnv('CI_BOT_PASSWORD');
  const botDisplayName = process.env.CI_BOT_DISPLAY_NAME || 'CI Bot';

  console.log('Logging in as Admin...');
  const adminToken = await login(adminEmail, adminPassword);

  console.log('Creating a one-time invite for the CI Bot account...');
  const invite = await createInvite(adminToken, 1, 1); // 1 use, expires in 1 day

  console.log('Registering the CI Bot user...');
  await registerWithInvite(botEmail, botPassword, botDisplayName, invite.code);

  console.log('Promoting CI Bot to Contributor...');
  const users = await listUsers(adminToken);
  const bot = users.find(u => u.email.toLowerCase() === botEmail.toLowerCase());
  if (!bot) throw new Error('Could not find the CI Bot user right after registering it.');
  await setUserRole(adminToken, bot.id, 'Contributor');

  console.log('Creating a Staging Environment Target (edit the placeholder URLs in this script first if needed)...');
  const env = await createEnvironment(adminToken, {
    name: process.env.ENV_NAME || 'Staging',
    tenant: process.env.ENV_TENANT || '',
    environmentType: 'Staging', // deliberately never "Production" for an automation target
    dashboardBaseUrl: process.env.ENV_DASHBOARD_URL || 'https://staging-dashboard.example.com',
    appApiBaseUrl: process.env.ENV_API_URL || 'https://staging-api.example.com',
    appBaseUrl: process.env.ENV_APP_URL || 'https://staging-app.example.com',
    masterDbConnectionString: null,
    transactionDbConnectionString: null,
    reportDbConnectionString: null,
    requiresTestDataCleanup: true
  });

  console.log('Generating a CI API Key (scope: ReportResults)...');
  const key = await createApiKey(adminToken, process.env.API_KEY_NAME || 'CI Pipeline', 'ReportResults');

  console.log('\n=====================================================================');
  console.log('DONE. Save these as CI secrets NOW -- the raw API key is shown only once:');
  console.log('=====================================================================');
  console.log(`TCH_BASE_URL           = ${process.env.TCH_BASE_URL || 'https://testcasehub.onrender.com'}`);
  console.log(`TCH_CI_BOT_EMAIL       = ${botEmail}`);
  console.log(`TCH_CI_BOT_PASSWORD    = ${botPassword}`);
  console.log(`TCH_API_KEY            = ${key.rawKey}`);
  console.log(`TCH_ENVIRONMENT_ID     = ${env.id}   (${env.name})`);
  console.log('=====================================================================\n');
}

function requireEnv(name) {
  const v = process.env[name];
  if (!v) { console.error(`Missing required env var: ${name}`); process.exit(1); }
  return v;
}

main().catch(err => { console.error(err); process.exit(1); });
