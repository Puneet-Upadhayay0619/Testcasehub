# Test Case Hub — Backend API

This is the shared backend for the Test Case Hub tool. The Cowork artifact
(`test_case_hub.html`) now talks to this API for everything — modules, test cases, task links,
history, and the extensible priority/status lists — instead of browser `localStorage`, so the
whole team sees the same data. Every endpoint below was actually called and verified against a
running instance of this exact code, not just written.

## Storage: JSON file by default, SQL Server when you want it

You do **not** need a database to run this. It defaults to a single JSON file (`data.json`,
created next to the running app on first request) behind a small `IDataStore` interface, so a
small team can run the whole thing on one server with zero setup. Flip one config value to move
to SQL Server later — same API, same frontend, no code changes needed on either side.

`appsettings.json`:
```json
"Storage": { "Mode": "JsonFile", "JsonFilePath": "data.json" }
```
Set `"Mode": "SqlServer"` and fill in `ConnectionStrings:SqlServer` to switch. A SQL-Server-flavored
EF Core migration (`Migrations/`) is already generated and ready to run via `dotnet ef database
update`, or the app will run it automatically on startup when `Mode` is `SqlServer`.

**Important caveat for JSON-file mode**: it's safe for one running server process (a single
in-process lock guards every read/write, with atomic temp-file-then-rename writes so a crash
mid-write can't corrupt the file). It is *not* safe if you ever run multiple instances behind a
load balancer — move to SQL Server first if you need that.

## What's included

- **Auth**: `POST /api/auth/register`, `POST /api/auth/login` — email/password, BCrypt-hashed,
  JWT bearer tokens (7-day expiry). This is also the login screen now built into
  `test_case_hub.html` — the first person to open the artifact against a fresh server registers
  an account; everyone else logs in with whatever they set.
- **Modules**: `GET/POST /api/modules`, `GET/POST /api/modules/{id}/task-links`.
- **Test cases**: full CRUD at `/api/testcases` (filterable by `moduleId`, `layer`,
  `verificationType`, `status`, `priority`, `search`), `POST /api/testcases/{id}/deprecate`,
  `GET /api/testcases/{id}/history` — validation matches the artifact exactly (Module/Task
  area/Verification type/Title required, every step needs both an action AND an expected
  result — a step you can't assert can't be automated, so it's rejected, not half-saved).
  Test case IDs (`TC-<module>-<area>-###`) are generated server-side on create.
- **Lookups**: `GET/POST /api/lookups/priorities`, `GET/POST /api/lookups/statuses` — anyone
  can add a new value from the "+ Add new priority/status" option in the artifact; it shows up
  for every teammate on their next load.
- Every create/update/deprecate writes an audit row to History with old/new snapshots, visible
  when a QA expands a test case row in the artifact.

## Running it locally

```
cd TestCaseHub.Api
dotnet run --urls http://localhost:5000
```
Swagger UI is at `/swagger` in Development mode. Then open `test_case_hub.html`, enter
`http://localhost:5000` as the API server URL on the login screen, and register.

## Deploying for real team use

1. Pick a machine/VM/container host your team can reach and run the app there (IIS, a Docker
   container, a small VM with `dotnet TestCaseHub.Api.dll` behind a process manager — any
   standard ASP.NET Core hosting approach works). Everyone points the artifact's "API server
   URL" field at that one address.
2. **Change `Jwt:Key`** in `appsettings.json` to a long random secret (32+ characters) — the
   placeholder in this repo must never be used in production.
3. **Restrict CORS** — it's currently `AllowAnyOrigin` for easy local testing. Lock it down to
   wherever the artifact is actually served from before going live.
4. If you outgrow the JSON file (multiple server instances, very large data, needing SQL
   queries over test case data), switch `Storage:Mode` to `SqlServer` as described above.
5. Back up `data.json` (or your SQL Server database) regularly — it's the only copy of your
   team's test cases.

## What's deliberately not done yet

- **No role-based permissions** — any logged-in user can edit any module's test cases.
  Module-ownership restrictions would need to be added on top of this.
- **Steps/Tags are stored as JSON text/columns**, not normalized child tables — fine at this
  scale; worth revisiting if you ever need to query/filter by individual step content.
- **No password reset / email verification flow** — register/login only.
- **JSON-file mode is single-instance only** (see caveat above).

## Verified end-to-end

Register → login → wrong-password rejection (401) → create module → create test case (correct
`TC-<module>-<area>-###` ID, exact field names/casing the frontend expects) → reject-on-missing
fields → list/filter by module → update (version increments) → deprecate → history shows all
changes with full old/new snapshots → add custom priority (persists, shows up in subsequent GET)
→ add task link → duplicate module code correctly rejected with 409 → full round trip re-run
against a live `dotnet run` instance immediately before packaging this build, using the exact
JSON payload shapes the rewired `test_case_hub.html` now sends.
