namespace TestCaseHub.Api.Services;

// Enforces the agreed DB-automation safety rule: any DB query configured for an automated
// check must be a SELECT — never something that could mutate the target system's data. This
// is the application-level half of the "SELECT-only" control; the other half (Phase 6 design)
// is using a dedicated read-only DB user/credential at the actual database level as
// defense-in-depth, which happens outside this codebase when an environment's connection
// string is provisioned.
public static class DbQuerySafety
{
    private static readonly string[] ForbiddenKeywords =
        { "insert", "update", "delete", "drop", "alter", "truncate", "merge", "exec", "execute", "create" };

    public static bool IsSelectOnly(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true; // no query configured — nothing to validate
        var trimmed = query.TrimStart().ToLowerInvariant();
        if (!trimmed.StartsWith("select")) return false;
        return !ForbiddenKeywords.Any(kw => System.Text.RegularExpressions.Regex.IsMatch(trimmed, $@"\b{kw}\b"));
    }
}
