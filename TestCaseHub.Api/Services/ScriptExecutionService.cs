using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Services;

// Real (hits the live environment) or Mock (uses each step's canned MockResponse/MockRows,
// touches no network/DB at all) -- lets a script's assertion LOGIC be validated deterministically
// against fixture data, independent of whether a real company/environment happens to be in the
// state the test assumes. Agreed in planning alongside Smoke/Sanity/Regression tiers.
public enum ExecutionMode { Real, Mock }

// One step of a script's native execution definition. This is a small, deliberately generic
// DSL -- NOT a TypeScript interpreter -- that covers exactly the patterns the 19 (and 8
// flagged) hand-written Playwright scripts for UWMC actually use: an HTTP call against the
// Dashboard API, a raw SQL query against the target DB, and an assertion against something
// saved earlier. Every field is optional except Type -- which fields matter depends on it.
public class ExecStep
{
    public string Type { get; set; } = ""; // "http" | "sql" | "sqlForEach" | "assert"

    // If true, this step still runs even after an earlier step in the same script has failed --
    // meant for teardown/restore steps (e.g. "put back the rows a setup step deleted") so
    // capture-and-restore is safe regardless of whether the test in between passed or failed.
    // Normal steps stop running (fail-fast) the moment any prior step fails, same as before.
    public bool AlwaysRun { get; set; } = false;

    // --- http ---
    public string? Method { get; set; }          // GET / POST
    public string? Path { get; set; }             // relative to EnvironmentTarget.DashboardBaseUrl
    public JsonElement? Body { get; set; }         // request body, sent as JSON
    public int? ExpectStatus { get; set; }         // if set, mismatch fails the step immediately
    public bool AuthRequired { get; set; } = true; // false for the "no Authorization header" DSH-027 case
    public string? SaveAs { get; set; }            // variable name -- stores { status, body, text }
    // Mock mode only: canned response body substituted for the real HTTP call. Ignored in Real mode.
    public JsonElement? MockResponse { get; set; }

    // --- sql / sqlForEach ---
    public string? Database { get; set; }          // "Master" | "Transaction" | "Report" (default Master)
    public string? Query { get; set; }
    public Dictionary<string, JsonElement>? Params { get; set; }
    // SaveAs reused -- stores a JSON array of row objects (column name -> value)
    // sqlForEach reuses Source (below) -- the saved variable (array of row objects, typically
    // captured by an earlier "sql" SELECT) to iterate; Query runs once per row with every JSON
    // property of that row bound as a same-named @param. This is what makes capture-and-restore
    // generic: capture existing rows, mutate/delete them for the test, then (AlwaysRun=true)
    // restore each one via an INSERT built straight from its own captured column values.
    // Mock mode only: canned rows substituted for the real SELECT (or, for sqlForEach, just
    // logged rather than executed -- Mock mode never touches a real DB). Ignored in Real mode.
    public JsonElement? MockRows { get; set; }

    // --- assert ---
    public string? Source { get; set; }            // variable name to read
    public string? ArrayField { get; set; }         // dot path within Source to an array (e.g. "body"); omit if Source itself is the array/object to check
    public Dictionary<string, JsonElement>? Find { get; set; } // locate one element of the array by field match
    public string? Field { get; set; }              // field to extract from the located element (or from ArrayField target directly if Find omitted)
    public string? Op { get; set; }                 // equals | notEquals | isTrue | isFalse | notNull | isNull | greaterThan | greaterOrEqual | lessThan | arrayLengthEquals | allMatch | noneMatch | stringEmpty
    public JsonElement? Expected { get; set; }

    // Optional: instead of a literal Expected, compare Target against ANOTHER variable resolved
    // the same way (Source/ArrayField/Find/Field) -- e.g. "the Get response's row for module=50
    // must equal what a earlier SQL query captured as its prior Visible value". When set, this
    // takes precedence over Expected for every Op below.
    public string? CompareSource { get; set; }
    public string? CompareArrayField { get; set; }
    public Dictionary<string, JsonElement>? CompareFind { get; set; }
    public string? CompareField { get; set; }

    public string? Label { get; set; }              // human-readable description for the run log
}

public class AssertionFailedException : Exception
{
    public AssertionFailedException(string message) : base(message) { }
}

public record ExecutionOutcome(bool Passed, string Status, List<string> Log, string? Error);

// Interpreter for ExecStep. Deliberately native C# (System.Net.Http + Microsoft.Data.SqlClient,
// both already referenced by this project) -- agreed explicitly: "test case hub se hi complete
// testing" means no Node.js/Playwright subprocess and no external tooling the user has to run
// themselves. This trades exact Playwright-script fidelity for something Test Case Hub can run
// end-to-end on its own hosting (Render, .NET only). The human-readable Content field (the real
// Playwright script, with its FLAG comments) is untouched and remains the source of truth for
// what SHOULD be tested -- ExecutionDefinitionJson is a hand-authored translation of it.
public class ScriptExecutionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    // Guardrail (agreed in planning after the DSH-031/046 cleanup-step discussion): a step whose
    // SQL text looks destructive is refused unless the EnvironmentTarget has explicitly
    // confirmed AllowDestructiveTestSql -- specifically to stop a misconfigured TestCompanyId
    // from silently deleting/updating some OTHER real company's saved data.
    private static readonly Regex DestructiveSqlPattern =
        new(@"\b(DELETE|UPDATE|INSERT|DROP|TRUNCATE|MERGE|ALTER|EXEC|EXECUTE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static bool IsDestructiveQuery(string query) => DestructiveSqlPattern.IsMatch(query);

    private readonly HttpClient _http;
    private readonly SecretProtector _protector;

    public ScriptExecutionService(HttpClient http, SecretProtector protector)
    {
        _http = http; _protector = protector;
    }

    public async Task<ExecutionOutcome> ExecuteAsync(AutomationScript script, EnvironmentTarget env, EnvironmentCredential? cred, ExecutionMode mode = ExecutionMode.Real)
    {
        var log = new List<string>();
        if (mode == ExecutionMode.Mock)
            log.Add("Running in MOCK mode -- no real network/DB calls will be made; every step uses its own MockResponse/MockRows.");

        if (string.IsNullOrWhiteSpace(script.ExecutionDefinitionJson))
            return new ExecutionOutcome(false, "Blocked", log,
                "This script has no native execution definition yet -- only the human-written Playwright script (Content) exists. It needs to be converted to the step DSL before Test Case Hub can run it natively.");

        // Mobile app ("App" layer) has no native execution path -- there is no in-process HTTP
        // client that can drive a real device/emulator the way RunHttpStepAsync drives a
        // Dashboard/App-API HTTP call. These results come from an external pipeline (GitHub
        // Actions + Appium/Espresso/XCUITest/Maestro on a device farm) posting back to the
        // existing CI-facing results endpoint instead -- see the Mobile App Automation
        // discussion notes. Blocked here defensively even though the UI already disables the Run
        // button for App-layer scripts, so a direct API/MCP call can't bypass it either.
        if (script.Layer == "App")
            return new ExecutionOutcome(false, "Blocked", log,
                "Native Run isn't available for App (mobile) scripts -- results are recorded via an external CI pipeline (GitHub Actions/device farm) posting to this Test Run instead, not by Test Case Hub's own runner.");

        List<ExecStep>? steps;
        try { steps = JsonSerializer.Deserialize<List<ExecStep>>(script.ExecutionDefinitionJson, JsonOpts); }
        catch (Exception ex)
        {
            return new ExecutionOutcome(false, "Blocked", log, $"ExecutionDefinitionJson is not valid JSON: {ex.Message}");
        }
        if (steps is null || steps.Count == 0)
            return new ExecutionOutcome(false, "Blocked", log, "ExecutionDefinitionJson has no steps.");

        // Documented limitation (agreed simplification for this MVP): rather than reverse-
        // engineering FieldAssist's real email+password login flow, the EnvironmentCredential's
        // encrypted "password" field is used directly as a pre-obtained Bearer token. A Lead
        // gets this token once (log into the Dashboard, copy it from devtools/localStorage) and
        // pastes it as that named credential's password in Test Case Hub -- from then on, Test
        // Case Hub itself makes every HTTP call, no external script/zip needed.
        string? bearerToken = cred is not null && !string.IsNullOrEmpty(cred.PasswordEncrypted)
            ? _protector.Unprotect(cred.PasswordEncrypted)
            : null;

        var vars = new Dictionary<string, JsonElement>();

        // Once a step fails, remaining NORMAL steps are skipped (fail-fast, same as before) --
        // but any step marked AlwaysRun (teardown/restore) still runs regardless, and the FIRST
        // failure encountered (test or teardown) is what gets reported as the outcome.
        string? firstFailureMessage = null;

        foreach (var step in steps)
        {
            var label = step.Label ?? step.Type;

            if (firstFailureMessage is not null && !step.AlwaysRun)
            {
                log.Add($"SKIP [{label}] (a prior step failed; not marked AlwaysRun)");
                continue;
            }

            try
            {
                switch (step.Type)
                {
                    case "http":
                        await RunHttpStepAsync(step, env, bearerToken, vars, log, mode, script.Layer);
                        break;
                    case "sql":
                        await RunSqlStepAsync(step, env, vars, log, mode);
                        break;
                    case "sqlForEach":
                        await RunSqlForEachStepAsync(step, env, vars, log, mode);
                        break;
                    case "assert":
                        RunAssertStep(step, vars, log);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown step type '{step.Type}'.");
                }
            }
            catch (AssertionFailedException afx)
            {
                log.Add($"FAIL [{label}]: {afx.Message}");
                firstFailureMessage ??= afx.Message;
            }
            catch (Exception ex)
            {
                log.Add($"ERROR [{label}]: {ex.Message}");
                firstFailureMessage ??= ex.Message;
            }
        }

        if (firstFailureMessage is not null)
            return new ExecutionOutcome(false, "Fail", log, firstFailureMessage);

        log.Add("All steps passed.");
        return new ExecutionOutcome(true, "Pass", log, null);
    }

    private async Task RunHttpStepAsync(ExecStep step, EnvironmentTarget env, string? bearerToken, Dictionary<string, JsonElement> vars, List<string> log, ExecutionMode mode, string? scriptLayer = null)
    {
        if (mode == ExecutionMode.Mock)
        {
            if (step.MockResponse is not JsonElement mockBody || mockBody.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"Running in Mock mode but this http step ('{step.Label ?? step.Path ?? "http"}') has no MockResponse defined.");
            log.Add($"MOCK {step.Method ?? "GET"} {step.Path}");
            if (!string.IsNullOrEmpty(step.SaveAs))
            {
                var wrapper = new Dictionary<string, object?> { ["status"] = 200, ["body"] = JsonSerializer.Deserialize<object?>(mockBody.GetRawText()), ["text"] = mockBody.GetRawText() };
                vars[step.SaveAs] = JsonSerializer.SerializeToElement(wrapper, JsonOpts);
            }
            log.Add("  -> 200 (mock)");
            return;
        }

        // App-API scripts hit the App API host (env.AppApiBaseUrl) instead of the Dashboard host
        // -- e.g. Dashboard's fa-dashboard-apis[-beta].fieldassist.io vs whatever the mobile
        // app's own backend is at. Everything else (Dashboard, or a script with no Layer set
        // yet) keeps hitting DashboardBaseUrl, same as before this existed.
        var useAppApi = scriptLayer == "App-API";
        var configuredUrl = useAppApi ? env.AppApiBaseUrl : env.DashboardBaseUrl;
        var urlFieldName = useAppApi ? "AppApiBaseUrl" : "DashboardBaseUrl";
        if (string.IsNullOrWhiteSpace(configuredUrl))
            throw new InvalidOperationException($"EnvironmentTarget has no {urlFieldName} configured.");
        var baseUrl = configuredUrl.TrimEnd('/');
        var path = step.Path ?? "";
        var url = baseUrl + (path.StartsWith("/") ? path : "/" + path);

        using var req = new HttpRequestMessage(new HttpMethod(step.Method ?? "GET"), url);
        if (step.AuthRequired && !string.IsNullOrEmpty(bearerToken))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        if (step.Body is JsonElement body && body.ValueKind != JsonValueKind.Undefined)
        {
            var substituted = SubstituteTemplates(JsonNode.Parse(body.GetRawText()), env);
            req.Content = new StringContent(substituted?.ToJsonString() ?? "null", Encoding.UTF8, "application/json");
        }

        log.Add($"{step.Method ?? "GET"} {path}");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var statusCode = (int)res.StatusCode;

        if (step.ExpectStatus is int expected && statusCode != expected)
            throw new AssertionFailedException($"{step.Method ?? "GET"} {path} returned {statusCode}, expected {expected}. Body: {Truncate(text)}");

        JsonElement bodyElement;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { bodyElement = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (Exception parseEx)
            {
                // Don't silently swallow this -- a 200 with a body that isn't valid JSON usually
                // means the request never reached the real API controller at all (an auth
                // redirect to an HTML login/SPA shell, a proxy/CDN error page, wrong base URL,
                // etc). Surfacing the raw text here is often the single most useful diagnostic
                // in the whole run, since "test case hub se hi complete testing" means this log
                // IS the only debugging tool available -- there's no browser devtools to fall
                // back on.
                log.Add($"  WARNING: response body is not valid JSON ({parseEx.Message}). Raw (truncated): {Truncate(text)}");
                bodyElement = JsonDocument.Parse("null").RootElement.Clone();
            }
        }
        else bodyElement = JsonDocument.Parse("null").RootElement.Clone();

        if (!string.IsNullOrEmpty(step.SaveAs))
        {
            var wrapper = new Dictionary<string, object?> { ["status"] = statusCode, ["body"] = JsonSerializer.Deserialize<object?>(bodyElement.GetRawText()), ["text"] = text };
            vars[step.SaveAs] = JsonSerializer.SerializeToElement(wrapper, JsonOpts);
        }
        log.Add($"  -> {statusCode}");
    }

    private async Task RunSqlStepAsync(ExecStep step, EnvironmentTarget env, Dictionary<string, JsonElement> vars, List<string> log, ExecutionMode mode)
    {
        if (string.IsNullOrWhiteSpace(step.Query)) throw new InvalidOperationException("SQL step has no Query.");

        if (mode == ExecutionMode.Mock)
        {
            if (step.MockRows is not JsonElement mockRows || mockRows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Running in Mock mode but this sql step ('{step.Label ?? "sql"}') has no MockRows array defined.");
            log.Add($"MOCK SQL ({step.Database ?? "Master"}): {Truncate(step.Query)}");
            if (!string.IsNullOrEmpty(step.SaveAs))
                vars[step.SaveAs] = mockRows;
            log.Add($"  -> {mockRows.GetArrayLength()} mock row(s)");
            return;
        }

        if (IsDestructiveQuery(step.Query) && !env.AllowDestructiveTestSql)
            throw new InvalidOperationException($"This step's SQL looks destructive (DELETE/UPDATE/INSERT/DROP/TRUNCATE/MERGE/etc.) but '{env.Name}' has not confirmed \"Allow destructive test SQL\". An Admin must explicitly enable that on the Environment Target first -- this exists specifically so a misconfigured TestCompanyId can never silently touch another real company's data. Query: {Truncate(step.Query)}");

        var encryptedConn = (step.Database ?? "Master") switch
        {
            "Transaction" => env.TransactionDbConnectionStringEncrypted,
            "Report" => env.ReportDbConnectionStringEncrypted,
            _ => env.MasterDbConnectionStringEncrypted,
        };
        if (string.IsNullOrWhiteSpace(encryptedConn))
            throw new InvalidOperationException($"EnvironmentTarget has no {(step.Database ?? "Master")} DB connection string configured.");
        var connStr = _protector.Unprotect(encryptedConn);

        log.Add($"SQL ({step.Database ?? "Master"}): {Truncate(step.Query)}");
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(step.Query, conn);
        if (step.Params is not null)
            foreach (var (key, val) in step.Params)
            {
                var substituted = SubstituteTemplates(JsonNode.Parse(val.GetRawText()), env);
                var clrValue = substituted is null ? null : JsonElementToClr(JsonSerializer.SerializeToElement(substituted, JsonOpts));
                cmd.Parameters.AddWithValue(key.StartsWith("@") ? key : "@" + key, clrValue ?? DBNull.Value);
            }

        var rows = new List<Dictionary<string, object?>>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }
        if (!string.IsNullOrEmpty(step.SaveAs))
            vars[step.SaveAs] = JsonSerializer.SerializeToElement(rows, JsonOpts);
        log.Add($"  -> {rows.Count} row(s)");
    }

    // "sqlForEach" -- iterates a captured rows variable (typically from an earlier "sql" SELECT
    // step) and runs Query once per row, binding every JSON property of that row as a same-named
    // @param. This is what makes capture-and-restore generic: capture existing rows into a
    // variable, mutate/delete them for the test, then (AlwaysRun=true) restore each captured row
    // via an INSERT built from its own column values -- no test-specific C# code needed.
    private async Task RunSqlForEachStepAsync(ExecStep step, EnvironmentTarget env, Dictionary<string, JsonElement> vars, List<string> log, ExecutionMode mode)
    {
        if (string.IsNullOrWhiteSpace(step.Query)) throw new InvalidOperationException("sqlForEach step has no Query.");

        // sqlForEach is very often AlwaysRun=true (a restore/teardown step) whose Source was
        // meant to be captured by an EARLIER step -- but if that earlier capture step itself
        // failed (e.g. a real DB connection error), it never got the chance to save anything,
        // so the variable genuinely doesn't exist. That's not a bug in THIS step: there's
        // nothing to restore, so treat a missing/non-array Source as "0 rows" and move on,
        // rather than throwing a confusing "unknown variable" error on top of the real failure.
        if (string.IsNullOrEmpty(step.Source) || !vars.TryGetValue(step.Source, out var rowsVar) || rowsVar.ValueKind != JsonValueKind.Array)
        {
            log.Add($"SKIP sqlForEach ({step.Label ?? "sqlForEach"}): Source '{step.Source}' was never captured (an earlier step likely failed before it could run) -- nothing to restore.");
            return;
        }

        if (mode == ExecutionMode.Mock)
        {
            log.Add($"MOCK sqlForEach ({step.Database ?? "Master"}): {Truncate(step.Query)} x {rowsVar.GetArrayLength()} row(s) from '{step.Source}' (no real DB touched)");
            return;
        }

        if (IsDestructiveQuery(step.Query) && !env.AllowDestructiveTestSql)
            throw new InvalidOperationException($"This sqlForEach step's SQL looks destructive but '{env.Name}' has not confirmed \"Allow destructive test SQL\". Query: {Truncate(step.Query)}");

        var encryptedConn = (step.Database ?? "Master") switch
        {
            "Transaction" => env.TransactionDbConnectionStringEncrypted,
            "Report" => env.ReportDbConnectionStringEncrypted,
            _ => env.MasterDbConnectionStringEncrypted,
        };
        if (string.IsNullOrWhiteSpace(encryptedConn))
            throw new InvalidOperationException($"EnvironmentTarget has no {(step.Database ?? "Master")} DB connection string configured.");
        var connStr = _protector.Unprotect(encryptedConn);

        log.Add($"SQL forEach ({step.Database ?? "Master"}): {Truncate(step.Query)} x {rowsVar.GetArrayLength()} row(s) from '{step.Source}'");
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        int count = 0;
        foreach (var row in rowsVar.EnumerateArray())
        {
            using var cmd = new SqlCommand(step.Query, conn);
            if (row.ValueKind == JsonValueKind.Object)
                foreach (var prop in row.EnumerateObject())
                    cmd.Parameters.AddWithValue("@" + prop.Name, JsonElementToClr(prop.Value) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            count++;
        }
        log.Add($"  -> executed for {count} row(s)");
    }

    private void RunAssertStep(ExecStep step, Dictionary<string, JsonElement> vars, List<string> log)
    {
        var target = ResolveTarget(vars, step.Source, step.ArrayField, step.Find, step.Field, step.Label ?? "Assertion", allowMissingFind: step.Op == "noneMatch");

        if (step.Op == "allMatch")
        {
            var arrayField = string.IsNullOrEmpty(step.ArrayField) ? GetVar(vars, step.Source) : GetByPath(GetVar(vars, step.Source), step.ArrayField);
            if (arrayField.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("allMatch requires ArrayField to resolve to a JSON array.");
            var expected = ResolveExpected(vars, step);
            foreach (var item in arrayField.EnumerateArray())
            {
                var actual = string.IsNullOrEmpty(step.Field) ? item : GetProperty(item, step.Field);
                if (!Compare(actual, "equals", expected))
                    throw new AssertionFailedException($"{step.Label ?? "Assertion"}: element {item.GetRawText()} failed allMatch on field '{step.Field}'.");
            }
            log.Add($"OK [{step.Label ?? "assert"}]: all {arrayField.GetArrayLength()} elements matched.");
            return;
        }

        if (step.Op == "noneMatch")
        {
            var arrayField = string.IsNullOrEmpty(step.ArrayField) ? GetVar(vars, step.Source) : GetByPath(GetVar(vars, step.Source), step.ArrayField);
            if (arrayField.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("noneMatch requires ArrayField to resolve to a JSON array.");
            if (step.Find is null) throw new InvalidOperationException("noneMatch requires Find.");
            foreach (var item in arrayField.EnumerateArray())
            {
                bool matches = step.Find.All(kv => item.TryGetProperty(kv.Key, out var v) && JsonElementsEqual(v, kv.Value));
                if (matches)
                    throw new AssertionFailedException($"{step.Label ?? "Assertion"}: an element unexpectedly matched {JsonSerializer.Serialize(step.Find, JsonOpts)} (e.g. {item.GetRawText()}).");
            }
            log.Add($"OK [{step.Label ?? "assert"}]: no element matched {JsonSerializer.Serialize(step.Find, JsonOpts)}.");
            return;
        }

        var expectedValue = ResolveExpected(vars, step);
        if (!Compare(target, step.Op ?? "equals", expectedValue))
            throw new AssertionFailedException($"{step.Label ?? "Assertion"}: expected {step.Op} {(expectedValue?.GetRawText() ?? "null")}, got {target.GetRawText()}.");

        log.Add($"OK [{step.Label ?? "assert"}]");
    }

    private static JsonElement GetVar(Dictionary<string, JsonElement> vars, string? name)
    {
        if (string.IsNullOrEmpty(name) || !vars.TryGetValue(name, out var v))
            throw new InvalidOperationException($"Assert step references unknown variable '{name}'.");
        return v;
    }

    // Shared resolver for both the primary Target (Source/ArrayField/Find/Field) and, when
    // CompareSource is set, the dynamic "Expected" side of a comparison -- same lookup logic
    // either way, just pointed at different step properties.
    private static JsonElement ResolveTarget(Dictionary<string, JsonElement> vars, string? source, string? arrayField, Dictionary<string, JsonElement>? find, string? field, string label, bool allowMissingFind = false)
    {
        var root = GetVar(vars, source);
        var target = string.IsNullOrEmpty(arrayField) ? root : GetByPath(root, arrayField);

        if (find is not null)
        {
            if (target.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Find requires ArrayField to resolve to a JSON array.");
            JsonElement? found = null;
            foreach (var item in target.EnumerateArray())
            {
                bool allMatch = find.All(kv => item.TryGetProperty(kv.Key, out var v) && JsonElementsEqual(v, kv.Value));
                if (allMatch) { found = item; break; }
            }
            if (found is null)
            {
                if (allowMissingFind) return JsonDocument.Parse("null").RootElement;
                throw new AssertionFailedException($"{label}: no element matched {JsonSerializer.Serialize(find)} in array of {target.GetArrayLength()}.");
            }
            target = string.IsNullOrEmpty(field) ? found.Value : GetProperty(found.Value, field);
        }
        else if (!string.IsNullOrEmpty(field))
        {
            target = GetProperty(target, field);
        }
        return target;
    }

    private static JsonElement? ResolveExpected(Dictionary<string, JsonElement> vars, ExecStep step)
    {
        if (string.IsNullOrEmpty(step.CompareSource)) return step.Expected;
        return ResolveTarget(vars, step.CompareSource, step.CompareArrayField, step.CompareFind, step.CompareField, step.Label ?? "Assertion");
    }

    // --- helpers ---

    private static JsonElement GetByPath(JsonElement root, string path)
    {
        var cur = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            cur = GetProperty(cur, segment);
        return cur;
    }

    private static JsonElement GetProperty(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var v) ? v : JsonDocument.Parse("null").RootElement;

    private static bool JsonElementsEqual(JsonElement a, JsonElement b) => a.GetRawText() == b.GetRawText()
        || (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number && a.GetDouble() == b.GetDouble());

    private static object? JsonElementToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.GetRawText(),
    };

    private static bool Compare(JsonElement actual, string op, JsonElement? expected) => op switch
    {
        "equals" => expected is JsonElement e && JsonElementsEqual(actual, e),
        "notEquals" => !(expected is JsonElement e2 && JsonElementsEqual(actual, e2)),
        "isTrue" => actual.ValueKind == JsonValueKind.True,
        "isFalse" => actual.ValueKind == JsonValueKind.False,
        "notNull" => actual.ValueKind != JsonValueKind.Null && actual.ValueKind != JsonValueKind.Undefined,
        "isNull" => actual.ValueKind == JsonValueKind.Null || actual.ValueKind == JsonValueKind.Undefined,
        "stringEmpty" => actual.ValueKind == JsonValueKind.String && actual.GetString() == "",
        "greaterThan" => CompareNumericOrDate(actual, expected) > 0,
        "greaterOrEqual" => CompareNumericOrDate(actual, expected) >= 0,
        "lessThan" => CompareNumericOrDate(actual, expected) < 0,
        "arrayLengthEquals" => actual.ValueKind == JsonValueKind.Array && expected is JsonElement le && actual.GetArrayLength() == le.GetInt32(),
        _ => throw new InvalidOperationException($"Unknown assert op '{op}'."),
    };

    private static double CompareNumericOrDate(JsonElement actual, JsonElement? expected)
    {
        if (expected is null) throw new InvalidOperationException("Comparison op requires an Expected value.");
        if (actual.ValueKind == JsonValueKind.Number && expected.Value.ValueKind == JsonValueKind.Number)
            return actual.GetDouble() - expected.Value.GetDouble();
        if (actual.ValueKind == JsonValueKind.String && expected.Value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(actual.GetString(), out var da) && DateTime.TryParse(expected.Value.GetString(), out var db))
            return (da - db).TotalMilliseconds;
        throw new InvalidOperationException("Cannot numerically/temporally compare the given values.");
    }

    private static string Truncate(string s, int max = 300) => s.Length <= max ? s : s.Substring(0, max) + "...";

    // Recursively resolves "{{TestCompanyId}}" template tokens against the EnvironmentTarget's
    // configured TestCompanyId -- kept generic (a switch on token name) so a future template
    // var doesn't need a second traversal function. Throws a clear error if a step references a
    // token the environment hasn't configured, rather than silently sending a null/garbage value.
    private static JsonNode? SubstituteTemplates(JsonNode? node, EnvironmentTarget env)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s switch
            {
                "{{TestCompanyId}}" => env.TestCompanyId is int cid
                    ? JsonValue.Create(cid)
                    : throw new InvalidOperationException("This step references {{TestCompanyId}} but the EnvironmentTarget has no TestCompanyId configured (Admin: PATCH /api/environments/{id}/test-company-id)."),
                "{{TestCompanyBId}}" => env.TestCompanyBId is int cidB
                    ? JsonValue.Create(cidB)
                    : throw new InvalidOperationException("This step references {{TestCompanyBId}} but the EnvironmentTarget has no TestCompanyBId configured."),
                "{{TestReservedModuleEnum}}" => env.TestReservedModuleEnum is int mod
                    ? JsonValue.Create(mod)
                    : throw new InvalidOperationException("This step references {{TestReservedModuleEnum}} but the EnvironmentTarget has no TestReservedModuleEnum configured."),
                _ => JsonValue.Create(s),
            };
        }
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (key, val) in obj) result[key] = SubstituteTemplates(val?.DeepClone(), env);
            return result;
        }
        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr) result.Add(SubstituteTemplates(item?.DeepClone(), env));
            return result;
        }
        return node?.DeepClone();
    }
}
