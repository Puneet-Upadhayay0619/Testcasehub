// Thin client for TestCaseHub's REST API -- used by every automation script in this folder so
// the request/response shapes only live in one place. Two credential styles are supported
// because the backend itself supports two:
//   - JWT (email+password login) -- required for anything role-gated: creating a Test Run,
//     filing a bug from a failed result, reading test cases/environments/suites.
//   - X-Api-Key -- only accepted by POST /results/automated (the one deliberately
//     [AllowAnonymous] endpoint), so a CI job can report results without holding a real
//     user password as a secret. It does NOT work for any other endpoint -- see README.
//
// Field names below match exactly what the live API returns (verified against a running
// instance during development), not guessed from source.

const BASE_URL = process.env.TCH_BASE_URL || 'https://testcasehub.onrender.com';

async function request(path, { method = 'GET', token, apiKey, body } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  if (apiKey) headers['X-Api-Key'] = apiKey;

  const res = await fetch(`${BASE_URL}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined
  });

  const rawText = await res.text();
  let data = null;
  try { data = rawText ? JSON.parse(rawText) : null; } catch { /* plain-text error body */ }

  if (!res.ok) {
    const msg = (data && (data.title || data.message || (typeof data === 'string' && data))) || rawText || `HTTP ${res.status}`;
    throw new Error(`${method} ${path} -> ${res.status}: ${msg}`);
  }
  return data;
}

// ---- Auth ----
async function login(email, password) {
  const res = await request('/api/auth/login', { method: 'POST', body: { email, password } });
  return res.token; // JWT
}

// ---- Invites + user bootstrap (used by setup-once.js) ----
const createInvite = (token, maxUses, expiresInDays) =>
  request('/api/invites', { method: 'POST', token, body: { maxUses, expiresInDays } });

const registerWithInvite = (email, password, displayName, inviteCode) =>
  request('/api/auth/register', { method: 'POST', body: { email, password, displayName, inviteCode } });

const setUserRole = (token, userId, role) =>
  request(`/api/users/${userId}/access`, { method: 'PUT', token, body: { role } });

const listUsers = (token) => request('/api/users', { token });

// ---- Environment Targets ----
const listEnvironments = (token) => request('/api/environments', { token });
const createEnvironment = (token, payload) => request('/api/environments', { method: 'POST', token, body: payload });

// ---- API Keys ----
const createApiKey = (token, name, scope) =>
  request('/api/apikeys', { method: 'POST', token, body: { name, scope: scope || 'ReportResults' } });

// ---- Test cases (drives the declarative API-check runner) ----
const listTestCases = (token, filter = {}) => {
  const q = new URLSearchParams(Object.entries(filter).filter(([, v]) => v != null && v !== '')).toString();
  return request(`/api/testcases${q ? '?' + q : ''}`, { token });
};

// ---- Suites / Releases ----
const listSuites = (token) => request('/api/suites', { token });
const resolveSuite = (token, suiteId) => request(`/api/suites/${suiteId}/resolve`, { token });
const listReleases = (token) => request('/api/releases', { token });

// ---- Test Runs ----
const createTestRun = (token, payload) => request('/api/testruns', { method: 'POST', token, body: payload });

// useApiKey=true (default) -- the CI-friendly path. Pass false to use a JWT instead.
const reportAutomatedResult = (credential, runId, payload, useApiKey = true) =>
  request(`/api/testruns/${runId}/results/automated`, {
    method: 'POST',
    ...(useApiKey ? { apiKey: credential } : { token: credential }),
    body: payload
  });

const getResults = (token, runId) => request(`/api/testruns/${runId}/results`, { token });
const getRollup = (token, runId) => request(`/api/testruns/${runId}/rollup`, { token });

// Bug-filing is Contributor+ and JWT-only (API keys are not accepted here) -- see README.
const createBug = (token, runId, resultId) =>
  request(`/api/testruns/${runId}/results/${resultId}/create-bug`, { method: 'POST', token });

module.exports = {
  BASE_URL, request, login,
  createInvite, registerWithInvite, setUserRole, listUsers,
  listEnvironments, createEnvironment,
  createApiKey,
  listTestCases,
  listSuites, resolveSuite, listReleases,
  createTestRun, reportAutomatedResult, getResults, getRollup, createBug
};
