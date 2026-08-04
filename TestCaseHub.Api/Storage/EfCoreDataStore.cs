using Microsoft.EntityFrameworkCore;
using TestCaseHub.Api.Data;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Storage;

public class EfCoreDataStore : IDataStore
{
    private readonly AppDbContext _db;
    public EfCoreDataStore(AppDbContext db) => _db = db;

    public Task<User?> GetUserByEmailAsync(string email) => _db.Users.FirstOrDefaultAsync(u => u.Email == email);
    public Task<User?> GetUserByIdAsync(int id) => _db.Users.FirstOrDefaultAsync(u => u.Id == id);
    public async Task<User> CreateUserAsync(User user) { _db.Users.Add(user); await _db.SaveChangesAsync(); return user; }
    public Task<List<User>> GetUsersAsync() => _db.Users.ToListAsync();
    public async Task<User> UpdateUserAsync(User user) { _db.Users.Update(user); await _db.SaveChangesAsync(); return user; }
    public Task<int> CountUsersAsync() => _db.Users.CountAsync();

    public Task<List<Module>> GetModulesAsync() => _db.Modules.ToListAsync();
    public Task<Module?> GetModuleAsync(int id) => _db.Modules.FirstOrDefaultAsync(m => m.Id == id);
    public Task<bool> ModuleCodeExistsAsync(string code) => _db.Modules.AnyAsync(m => m.Code == code);
    public async Task<Module> CreateModuleAsync(Module module) { _db.Modules.Add(module); await _db.SaveChangesAsync(); return module; }

    public Task<Dictionary<int, int>> GetTestCaseCountsByModuleAsync() =>
        _db.TestCases.Where(t => t.Status != "Deprecated").GroupBy(t => t.ModuleId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);

    public async Task<TaskLink> CreateTaskLinkAsync(TaskLink link) { _db.TaskLinks.Add(link); await _db.SaveChangesAsync(); return link; }
    public Task<List<TaskLink>> GetTaskLinksAsync(int moduleId) => _db.TaskLinks.Where(l => l.ModuleId == moduleId).ToListAsync();

    public Task<List<TestCase>> GetTestCasesAsync(TestCaseFilter filter)
    {
        var query = _db.TestCases.AsQueryable();
        if (filter.ModuleId.HasValue) query = query.Where(t => t.ModuleId == filter.ModuleId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Layer)) query = query.Where(t => t.Layer == filter.Layer);
        if (!string.IsNullOrWhiteSpace(filter.VerificationType)) query = query.Where(t => t.VerificationType == filter.VerificationType);
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(t => t.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Priority)) query = query.Where(t => t.Priority == filter.Priority);
        if (!string.IsNullOrWhiteSpace(filter.Search)) query = query.Where(t => t.Title.Contains(filter.Search) || t.Id.Contains(filter.Search));
        return query.OrderByDescending(t => t.UpdatedAt).ToListAsync();
    }

    public Task<TestCase?> GetTestCaseAsync(string id) => _db.TestCases.FirstOrDefaultAsync(t => t.Id == id);
    public Task<int> CountTestCasesWithPrefixAsync(string prefix) => _db.TestCases.CountAsync(t => t.Id.StartsWith(prefix));

    public async Task<TestCase> CreateTestCaseAsync(TestCase tc) { _db.TestCases.Add(tc); await _db.SaveChangesAsync(); return tc; }
    public async Task<TestCase> UpdateTestCaseAsync(TestCase tc) { await _db.SaveChangesAsync(); return tc; }

    public async Task AddHistoryAsync(TestCaseHistory history) { _db.History.Add(history); await _db.SaveChangesAsync(); }
    public Task<List<TestCaseHistory>> GetHistoryAsync(string testCaseId) =>
        _db.History.Where(h => h.TestCaseId == testCaseId).OrderByDescending(h => h.ChangedAt).ToListAsync();

    public Task<List<string>> GetPrioritiesAsync() => _db.Priorities.OrderBy(p => p.Id).Select(p => p.Value).ToListAsync();
    public Task<bool> PriorityExistsAsync(string value) => _db.Priorities.AnyAsync(p => p.Value.ToLower() == value.ToLower());
    public async Task AddPriorityAsync(string value) { _db.Priorities.Add(new PriorityOption { Value = value, IsCustom = true }); await _db.SaveChangesAsync(); }

    public Task<List<string>> GetStatusesAsync() => _db.Statuses.OrderBy(s => s.Id).Select(s => s.Value).ToListAsync();
    public Task<bool> StatusExistsAsync(string value) => _db.Statuses.AnyAsync(s => s.Value.ToLower() == value.ToLower());
    public async Task AddStatusAsync(string value) { _db.Statuses.Add(new StatusOption { Value = value, IsCustom = true }); await _db.SaveChangesAsync(); }

    public async Task AddAuditLogAsync(AuditLog entry) { _db.AuditLogs.Add(entry); await _db.SaveChangesAsync(); }
    public Task<List<AuditLog>> GetAuditLogsAsync(int take = 200) =>
        _db.AuditLogs.OrderByDescending(a => a.OccurredAt).Take(take).ToListAsync();

    public async Task<InviteLink> CreateInviteLinkAsync(InviteLink invite) { _db.InviteLinks.Add(invite); await _db.SaveChangesAsync(); return invite; }
    public Task<InviteLink?> GetInviteLinkByCodeAsync(string code) => _db.InviteLinks.FirstOrDefaultAsync(i => i.Code == code);
    public Task<List<InviteLink>> GetInviteLinksAsync() => _db.InviteLinks.OrderByDescending(i => i.CreatedAt).ToListAsync();
    public async Task<InviteLink> UpdateInviteLinkAsync(InviteLink invite) { await _db.SaveChangesAsync(); return invite; }

    public async Task<RefreshToken> CreateRefreshTokenAsync(RefreshToken token) { _db.RefreshTokens.Add(token); await _db.SaveChangesAsync(); return token; }
    public Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash) => _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash);
    public async Task<RefreshToken> UpdateRefreshTokenAsync(RefreshToken token) { await _db.SaveChangesAsync(); return token; }
    public async Task RevokeAllRefreshTokensForUserAsync(int userId)
    {
        var tokens = await _db.RefreshTokens.Where(r => r.UserId == userId && !r.Revoked).ToListAsync();
        foreach (var t in tokens) t.Revoked = true;
        await _db.SaveChangesAsync();
    }

    public async Task<PasswordResetToken> CreatePasswordResetTokenAsync(PasswordResetToken token) { _db.PasswordResetTokens.Add(token); await _db.SaveChangesAsync(); return token; }
    public Task<PasswordResetToken?> GetPasswordResetTokenByHashAsync(string tokenHash) => _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
    public async Task<PasswordResetToken> UpdatePasswordResetTokenAsync(PasswordResetToken token) { await _db.SaveChangesAsync(); return token; }

    public Task<List<TestSuite>> GetSuitesAsync() => _db.TestSuites.OrderBy(s => s.Name).ToListAsync();
    public Task<TestSuite?> GetSuiteAsync(int id) => _db.TestSuites.FirstOrDefaultAsync(s => s.Id == id);
    public async Task<TestSuite> CreateSuiteAsync(TestSuite suite) { _db.TestSuites.Add(suite); await _db.SaveChangesAsync(); return suite; }
    public async Task<TestSuite> UpdateSuiteAsync(TestSuite suite) { await _db.SaveChangesAsync(); return suite; }

    public Task<List<TestCaseComment>> GetCommentsAsync(string testCaseId) =>
        _db.Comments.Where(c => c.TestCaseId == testCaseId).OrderBy(c => c.CreatedAt).ToListAsync();
    public async Task<TestCaseComment> AddCommentAsync(TestCaseComment comment) { _db.Comments.Add(comment); await _db.SaveChangesAsync(); return comment; }
    public Task<TestCaseComment?> GetCommentAsync(int id) => _db.Comments.FirstOrDefaultAsync(c => c.Id == id);
    public async Task<TestCaseComment> UpdateCommentAsync(TestCaseComment comment) { await _db.SaveChangesAsync(); return comment; }

    public Task<List<Release>> GetReleasesAsync() => _db.Releases.OrderByDescending(r => r.CreatedAt).ToListAsync();
    public Task<Release?> GetReleaseAsync(int id) => _db.Releases.FirstOrDefaultAsync(r => r.Id == id);
    public async Task<Release> CreateReleaseAsync(Release release) { _db.Releases.Add(release); await _db.SaveChangesAsync(); return release; }
    public async Task<Release> UpdateReleaseAsync(Release release) { await _db.SaveChangesAsync(); return release; }

    public Task<List<TestRun>> GetTestRunsAsync(int? releaseId) =>
        _db.TestRuns.Where(t => releaseId == null || t.ReleaseId == releaseId).OrderByDescending(t => t.CreatedAt).ToListAsync();
    public Task<TestRun?> GetTestRunAsync(int id) => _db.TestRuns.FirstOrDefaultAsync(t => t.Id == id);
    public async Task<TestRun> CreateTestRunAsync(TestRun run) { _db.TestRuns.Add(run); await _db.SaveChangesAsync(); return run; }

    public Task<List<TestRunResult>> GetTestRunResultsAsync(int testRunId) =>
        _db.TestRunResults.Where(r => r.TestRunId == testRunId).OrderBy(r => r.ExecutedAt).ToListAsync();
    public Task<TestRunResult?> GetTestRunResultByAttemptKeyAsync(string attemptKey) =>
        _db.TestRunResults.FirstOrDefaultAsync(r => r.RunAttemptKey == attemptKey);
    public async Task<TestRunResult> AddTestRunResultAsync(TestRunResult result) { _db.TestRunResults.Add(result); await _db.SaveChangesAsync(); return result; }
    public async Task<TestRunResult> UpdateTestRunResultAsync(TestRunResult result) { await _db.SaveChangesAsync(); return result; }
    public Task<List<TestRunResult>> GetResultsForTestCaseAsync(string testCaseId) =>
        _db.TestRunResults.Where(r => r.TestCaseId == testCaseId).OrderBy(r => r.ExecutedAt).ToListAsync();
    public async Task<List<TestRunResult>> GetResultsForReleaseAsync(int releaseId)
    {
        var runIds = await _db.TestRuns.Where(t => t.ReleaseId == releaseId).Select(t => t.Id).ToListAsync();
        return await _db.TestRunResults.Where(r => runIds.Contains(r.TestRunId)).OrderBy(r => r.ExecutedAt).ToListAsync();
    }

    public async Task<Notification> AddNotificationAsync(Notification n) { _db.Notifications.Add(n); await _db.SaveChangesAsync(); return n; }
    public Task<List<Notification>> GetNotificationsAsync(int userId, bool unreadOnly) =>
        _db.Notifications.Where(n => n.UserId == userId && (!unreadOnly || !n.Read)).OrderByDescending(n => n.CreatedAt).ToListAsync();
    public Task<Notification?> GetNotificationAsync(int id) => _db.Notifications.FirstOrDefaultAsync(n => n.Id == id);
    public async Task<Notification> UpdateNotificationAsync(Notification n) { await _db.SaveChangesAsync(); return n; }
    public Task<List<User>> GetUsersByRoleAsync(string role) => _db.Users.Where(u => u.Role == role && u.IsActive).ToListAsync();

    public async Task<ApiKey> CreateApiKeyAsync(ApiKey key) { _db.ApiKeys.Add(key); await _db.SaveChangesAsync(); return key; }
    public Task<List<ApiKey>> GetApiKeysAsync() => _db.ApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
    public Task<ApiKey?> GetApiKeyByHashAsync(string hash) => _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash);
    public Task<ApiKey?> GetApiKeyAsync(int id) => _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id);
    public async Task<ApiKey> UpdateApiKeyAsync(ApiKey key) { await _db.SaveChangesAsync(); return key; }

    public Task<List<EnvironmentTarget>> GetEnvironmentTargetsAsync() => _db.EnvironmentTargets.OrderBy(e => e.Name).ToListAsync();
    public Task<EnvironmentTarget?> GetEnvironmentTargetAsync(int id) => _db.EnvironmentTargets.FirstOrDefaultAsync(e => e.Id == id);
    public async Task<EnvironmentTarget> CreateEnvironmentTargetAsync(EnvironmentTarget env) { _db.EnvironmentTargets.Add(env); await _db.SaveChangesAsync(); return env; }
    public async Task<EnvironmentTarget> UpdateEnvironmentTargetAsync(EnvironmentTarget env) { await _db.SaveChangesAsync(); return env; }
}
