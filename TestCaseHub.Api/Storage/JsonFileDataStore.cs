using System.Text.Json;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Storage;

// Everything lives in one JSON file on disk instead of a database. A single in-process lock
// wraps every read AND write — at this tool's scale (a QA team, not a high-traffic service)
// this is simple and safe, and it removes any need to reason about partial/interleaved writes.
// IMPORTANT: this only works correctly with a single running instance of the API. If you ever
// scale this out to multiple instances behind a load balancer, each instance would have its own
// lock and its own in-memory copy — switch to the SQL Server store (Storage:Mode) at that point.
public class JsonFileDataStore : IDataStore
{
    private class AppData
    {
        public List<User> Users { get; set; } = new();
        public List<Module> Modules { get; set; } = new();
        public List<TaskLink> TaskLinks { get; set; } = new();
        public List<TestCase> TestCases { get; set; } = new();
        public List<TestCaseHistory> History { get; set; } = new();
        public List<PriorityOption> Priorities { get; set; } = new();
        public List<StatusOption> Statuses { get; set; } = new();
        public List<AuditLog> AuditLogs { get; set; } = new();
        public List<InviteLink> InviteLinks { get; set; } = new();
        public List<RefreshToken> RefreshTokens { get; set; } = new();
        public List<PasswordResetToken> PasswordResetTokens { get; set; } = new();
        public List<TestSuite> Suites { get; set; } = new();
        public List<TestCaseComment> Comments { get; set; } = new();
        public List<Release> Releases { get; set; } = new();
        public List<TestRun> TestRuns { get; set; } = new();
        public List<TestRunResult> TestRunResults { get; set; } = new();
        public List<Notification> Notifications { get; set; } = new();
        public List<ApiKey> ApiKeys { get; set; } = new();
        public List<EnvironmentTarget> EnvironmentTargets { get; set; } = new();
        public int NextUserId { get; set; } = 1;
        public int NextAuditLogId { get; set; } = 1;
        public int NextInviteLinkId { get; set; } = 1;
        public int NextRefreshTokenId { get; set; } = 1;
        public int NextPasswordResetTokenId { get; set; } = 1;
        public int NextSuiteId { get; set; } = 1;
        public int NextCommentId { get; set; } = 1;
        public int NextReleaseId { get; set; } = 1;
        public int NextTestRunId { get; set; } = 1;
        public int NextTestRunResultId { get; set; } = 1;
        public int NextNotificationId { get; set; } = 1;
        public int NextApiKeyId { get; set; } = 1;
        public int NextEnvironmentTargetId { get; set; } = 1;
        public int NextModuleId { get; set; } = 1;
        public int NextTaskLinkId { get; set; } = 1;
        public int NextHistoryId { get; set; } = 1;
        public int NextPriorityId { get; set; } = 1;
        public int NextStatusId { get; set; } = 1;
    }

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppData _data = new();
    private bool _loaded;

    public JsonFileDataStore(IConfiguration config)
    {
        _filePath = config["Storage:JsonFilePath"] ?? "data.json";
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        if (File.Exists(_filePath))
        {
            var json = await File.ReadAllTextAsync(_filePath);
            _data = string.IsNullOrWhiteSpace(json) ? new AppData() : (JsonSerializer.Deserialize<AppData>(json) ?? new AppData());
        }
        else
        {
            _data = new AppData
            {
                Priorities = new() {
                    new PriorityOption { Id = 1, Value = "P1", IsCustom = false },
                    new PriorityOption { Id = 2, Value = "P2", IsCustom = false },
                    new PriorityOption { Id = 3, Value = "P3", IsCustom = false },
                    new PriorityOption { Id = 4, Value = "P4", IsCustom = false }
                },
                Statuses = new() {
                    new StatusOption { Id = 1, Value = "Draft", IsCustom = false },
                    new StatusOption { Id = 2, Value = "Reviewed", IsCustom = false },
                    new StatusOption { Id = 3, Value = "Active", IsCustom = false },
                    new StatusOption { Id = 4, Value = "Deprecated", IsCustom = false }
                },
                NextPriorityId = 5,
                NextStatusId = 5
            };
            await PersistAsync();
        }
        _loaded = true;
    }

    // Atomic-ish write: write to a temp file then rename over the real one, so a crash
    // mid-write can't leave a half-written, corrupt data.json behind.
    private async Task PersistAsync()
    {
        var tmpPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tmpPath, json);
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            return await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<User?> GetUserByEmailAsync(string email) =>
        WithLockAsync(async () => _data.Users.FirstOrDefault(u => u.Email == email));

    public Task<User?> GetUserByIdAsync(int id) =>
        WithLockAsync(async () => _data.Users.FirstOrDefault(u => u.Id == id));

    public Task<User> CreateUserAsync(User user) =>
        WithLockAsync(async () =>
        {
            user.Id = _data.NextUserId++;
            _data.Users.Add(user);
            await PersistAsync();
            return user;
        });

    public Task<List<User>> GetUsersAsync() =>
        WithLockAsync(async () => _data.Users.ToList());

    public Task<User> UpdateUserAsync(User user) =>
        WithLockAsync(async () =>
        {
            var idx = _data.Users.FindIndex(u => u.Id == user.Id);
            if (idx >= 0) _data.Users[idx] = user;
            await PersistAsync();
            return user;
        });

    public Task<int> CountUsersAsync() =>
        WithLockAsync(async () => _data.Users.Count);

    public Task<List<Module>> GetModulesAsync() =>
        WithLockAsync(async () => _data.Modules.ToList());

    public Task<Module?> GetModuleAsync(int id) =>
        WithLockAsync(async () => _data.Modules.FirstOrDefault(m => m.Id == id));

    public Task<bool> ModuleCodeExistsAsync(string code) =>
        WithLockAsync(async () => _data.Modules.Any(m => m.Code == code));

    public Task<Module> CreateModuleAsync(Module module) =>
        WithLockAsync(async () =>
        {
            module.Id = _data.NextModuleId++;
            _data.Modules.Add(module);
            await PersistAsync();
            return module;
        });

    public Task<Dictionary<int, int>> GetTestCaseCountsByModuleAsync() =>
        WithLockAsync(async () => _data.TestCases
            .Where(t => t.Status != "Deprecated")
            .GroupBy(t => t.ModuleId)
            .ToDictionary(g => g.Key, g => g.Count()));

    public Task<TaskLink> CreateTaskLinkAsync(TaskLink link) =>
        WithLockAsync(async () =>
        {
            link.Id = _data.NextTaskLinkId++;
            _data.TaskLinks.Add(link);
            await PersistAsync();
            return link;
        });

    public Task<List<TaskLink>> GetTaskLinksAsync(int moduleId) =>
        WithLockAsync(async () => _data.TaskLinks.Where(l => l.ModuleId == moduleId).ToList());

    public Task<List<TestCase>> GetTestCasesAsync(TestCaseFilter filter) =>
        WithLockAsync(async () =>
        {
            var query = _data.TestCases.AsEnumerable();
            if (filter.ModuleId.HasValue) query = query.Where(t => t.ModuleId == filter.ModuleId.Value);
            if (!string.IsNullOrWhiteSpace(filter.Layer)) query = query.Where(t => t.Layer == filter.Layer);
            if (!string.IsNullOrWhiteSpace(filter.VerificationType)) query = query.Where(t => t.VerificationType == filter.VerificationType);
            if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(t => t.Status == filter.Status);
            if (!string.IsNullOrWhiteSpace(filter.Priority)) query = query.Where(t => t.Priority == filter.Priority);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search;
                query = query.Where(t => t.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || t.Id.Contains(s, StringComparison.OrdinalIgnoreCase));
            }
            return query.OrderByDescending(t => t.UpdatedAt).ToList();
        });

    public Task<TestCase?> GetTestCaseAsync(string id) =>
        WithLockAsync(async () => _data.TestCases.FirstOrDefault(t => t.Id == id));

    public Task<int> CountTestCasesWithPrefixAsync(string prefix) =>
        WithLockAsync(async () => _data.TestCases.Count(t => t.Id.StartsWith(prefix)));

    public Task<TestCase> CreateTestCaseAsync(TestCase tc) =>
        WithLockAsync(async () =>
        {
            _data.TestCases.Add(tc);
            await PersistAsync();
            return tc;
        });

    public Task<TestCase> UpdateTestCaseAsync(TestCase tc) =>
        WithLockAsync(async () =>
        {
            var idx = _data.TestCases.FindIndex(t => t.Id == tc.Id);
            if (idx >= 0) _data.TestCases[idx] = tc;
            await PersistAsync();
            return tc;
        });

    public Task AddHistoryAsync(TestCaseHistory history) =>
        WithLockAsync(async () =>
        {
            history.Id = _data.NextHistoryId++;
            _data.History.Add(history);
            await PersistAsync();
        });

    public Task<List<TestCaseHistory>> GetHistoryAsync(string testCaseId) =>
        WithLockAsync(async () => _data.History.Where(h => h.TestCaseId == testCaseId).OrderByDescending(h => h.ChangedAt).ToList());

    public Task<List<string>> GetPrioritiesAsync() =>
        WithLockAsync(async () => _data.Priorities.OrderBy(p => p.Id).Select(p => p.Value).ToList());

    public Task<bool> PriorityExistsAsync(string value) =>
        WithLockAsync(async () => _data.Priorities.Any(p => p.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

    public Task AddPriorityAsync(string value) =>
        WithLockAsync(async () =>
        {
            _data.Priorities.Add(new PriorityOption { Id = _data.NextPriorityId++, Value = value, IsCustom = true });
            await PersistAsync();
        });

    public Task<List<string>> GetStatusesAsync() =>
        WithLockAsync(async () => _data.Statuses.OrderBy(s => s.Id).Select(s => s.Value).ToList());

    public Task<bool> StatusExistsAsync(string value) =>
        WithLockAsync(async () => _data.Statuses.Any(s => s.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

    public Task AddStatusAsync(string value) =>
        WithLockAsync(async () =>
        {
            _data.Statuses.Add(new StatusOption { Id = _data.NextStatusId++, Value = value, IsCustom = true });
            await PersistAsync();
        });
    public Task AddAuditLogAsync(AuditLog entry) =>
        WithLockAsync(async () =>
        {
            entry.Id = _data.NextAuditLogId++;
            _data.AuditLogs.Add(entry);
            await PersistAsync();
        });

    public Task<List<AuditLog>> GetAuditLogsAsync(int take = 200) =>
        WithLockAsync(async () => _data.AuditLogs.OrderByDescending(a => a.OccurredAt).Take(take).ToList());

    public Task<InviteLink> CreateInviteLinkAsync(InviteLink invite) =>
        WithLockAsync(async () =>
        {
            invite.Id = _data.NextInviteLinkId++;
            _data.InviteLinks.Add(invite);
            await PersistAsync();
            return invite;
        });

    public Task<InviteLink?> GetInviteLinkByCodeAsync(string code) =>
        WithLockAsync(async () => _data.InviteLinks.FirstOrDefault(i => i.Code == code));

    public Task<List<InviteLink>> GetInviteLinksAsync() =>
        WithLockAsync(async () => _data.InviteLinks.OrderByDescending(i => i.CreatedAt).ToList());

    public Task<InviteLink> UpdateInviteLinkAsync(InviteLink invite) =>
        WithLockAsync(async () =>
        {
            var idx = _data.InviteLinks.FindIndex(i => i.Id == invite.Id);
            if (idx >= 0) _data.InviteLinks[idx] = invite;
            await PersistAsync();
            return invite;
        });
    public Task<RefreshToken> CreateRefreshTokenAsync(RefreshToken token) =>
        WithLockAsync(async () =>
        {
            token.Id = _data.NextRefreshTokenId++;
            _data.RefreshTokens.Add(token);
            await PersistAsync();
            return token;
        });

    public Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash) =>
        WithLockAsync(async () => _data.RefreshTokens.FirstOrDefault(r => r.TokenHash == tokenHash));

    public Task<RefreshToken> UpdateRefreshTokenAsync(RefreshToken token) =>
        WithLockAsync(async () =>
        {
            var idx = _data.RefreshTokens.FindIndex(r => r.Id == token.Id);
            if (idx >= 0) _data.RefreshTokens[idx] = token;
            await PersistAsync();
            return token;
        });

    public Task RevokeAllRefreshTokensForUserAsync(int userId) =>
        WithLockAsync(async () =>
        {
            foreach (var r in _data.RefreshTokens.Where(r => r.UserId == userId && !r.Revoked)) r.Revoked = true;
            await PersistAsync();
        });

    public Task<PasswordResetToken> CreatePasswordResetTokenAsync(PasswordResetToken token) =>
        WithLockAsync(async () =>
        {
            token.Id = _data.NextPasswordResetTokenId++;
            _data.PasswordResetTokens.Add(token);
            await PersistAsync();
            return token;
        });

    public Task<PasswordResetToken?> GetPasswordResetTokenByHashAsync(string tokenHash) =>
        WithLockAsync(async () => _data.PasswordResetTokens.FirstOrDefault(t => t.TokenHash == tokenHash));

    public Task<PasswordResetToken> UpdatePasswordResetTokenAsync(PasswordResetToken token) =>
        WithLockAsync(async () =>
        {
            var idx = _data.PasswordResetTokens.FindIndex(t => t.Id == token.Id);
            if (idx >= 0) _data.PasswordResetTokens[idx] = token;
            await PersistAsync();
            return token;
        });
    public Task<List<TestSuite>> GetSuitesAsync() =>
        WithLockAsync(async () => _data.Suites.OrderBy(s => s.Name).ToList());

    public Task<TestSuite?> GetSuiteAsync(int id) =>
        WithLockAsync(async () => _data.Suites.FirstOrDefault(s => s.Id == id));

    public Task<TestSuite> CreateSuiteAsync(TestSuite suite) =>
        WithLockAsync(async () =>
        {
            suite.Id = _data.NextSuiteId++;
            _data.Suites.Add(suite);
            await PersistAsync();
            return suite;
        });

    public Task<TestSuite> UpdateSuiteAsync(TestSuite suite) =>
        WithLockAsync(async () =>
        {
            var idx = _data.Suites.FindIndex(s => s.Id == suite.Id);
            if (idx >= 0) _data.Suites[idx] = suite;
            await PersistAsync();
            return suite;
        });

    public Task<List<TestCaseComment>> GetCommentsAsync(string testCaseId) =>
        WithLockAsync(async () => _data.Comments.Where(c => c.TestCaseId == testCaseId).OrderBy(c => c.CreatedAt).ToList());

    public Task<TestCaseComment> AddCommentAsync(TestCaseComment comment) =>
        WithLockAsync(async () =>
        {
            comment.Id = _data.NextCommentId++;
            _data.Comments.Add(comment);
            await PersistAsync();
            return comment;
        });

    public Task<TestCaseComment?> GetCommentAsync(int id) =>
        WithLockAsync(async () => _data.Comments.FirstOrDefault(c => c.Id == id));

    public Task<TestCaseComment> UpdateCommentAsync(TestCaseComment comment) =>
        WithLockAsync(async () =>
        {
            var idx = _data.Comments.FindIndex(c => c.Id == comment.Id);
            if (idx >= 0) _data.Comments[idx] = comment;
            await PersistAsync();
            return comment;
        });
    public Task<List<Release>> GetReleasesAsync() =>
        WithLockAsync(async () => _data.Releases.OrderByDescending(r => r.CreatedAt).ToList());

    public Task<Release?> GetReleaseAsync(int id) =>
        WithLockAsync(async () => _data.Releases.FirstOrDefault(r => r.Id == id));

    public Task<Release> CreateReleaseAsync(Release release) =>
        WithLockAsync(async () =>
        {
            release.Id = _data.NextReleaseId++;
            _data.Releases.Add(release);
            await PersistAsync();
            return release;
        });

    public Task<Release> UpdateReleaseAsync(Release release) =>
        WithLockAsync(async () =>
        {
            var idx = _data.Releases.FindIndex(r => r.Id == release.Id);
            if (idx >= 0) _data.Releases[idx] = release;
            await PersistAsync();
            return release;
        });

    public Task<List<TestRun>> GetTestRunsAsync(int? releaseId) =>
        WithLockAsync(async () => _data.TestRuns.Where(t => releaseId == null || t.ReleaseId == releaseId).OrderByDescending(t => t.CreatedAt).ToList());

    public Task<TestRun?> GetTestRunAsync(int id) =>
        WithLockAsync(async () => _data.TestRuns.FirstOrDefault(t => t.Id == id));

    public Task<TestRun> CreateTestRunAsync(TestRun run) =>
        WithLockAsync(async () =>
        {
            run.Id = _data.NextTestRunId++;
            _data.TestRuns.Add(run);
            await PersistAsync();
            return run;
        });

    public Task<List<TestRunResult>> GetTestRunResultsAsync(int testRunId) =>
        WithLockAsync(async () => _data.TestRunResults.Where(r => r.TestRunId == testRunId).OrderBy(r => r.ExecutedAt).ToList());

    public Task<TestRunResult?> GetTestRunResultByAttemptKeyAsync(string attemptKey) =>
        WithLockAsync(async () => _data.TestRunResults.FirstOrDefault(r => r.RunAttemptKey == attemptKey));

    public Task<TestRunResult> AddTestRunResultAsync(TestRunResult result) =>
        WithLockAsync(async () =>
        {
            result.Id = _data.NextTestRunResultId++;
            _data.TestRunResults.Add(result);
            await PersistAsync();
            return result;
        });

    public Task<TestRunResult> UpdateTestRunResultAsync(TestRunResult result) =>
        WithLockAsync(async () =>
        {
            var idx = _data.TestRunResults.FindIndex(r => r.Id == result.Id);
            if (idx >= 0) _data.TestRunResults[idx] = result;
            await PersistAsync();
            return result;
        });

    public Task<List<TestRunResult>> GetResultsForTestCaseAsync(string testCaseId) =>
        WithLockAsync(async () => _data.TestRunResults.Where(r => r.TestCaseId == testCaseId).OrderBy(r => r.ExecutedAt).ToList());

    public Task<List<TestRunResult>> GetResultsForReleaseAsync(int releaseId) =>
        WithLockAsync(async () =>
        {
            var runIds = _data.TestRuns.Where(t => t.ReleaseId == releaseId).Select(t => t.Id).ToHashSet();
            return _data.TestRunResults.Where(r => runIds.Contains(r.TestRunId)).OrderBy(r => r.ExecutedAt).ToList();
        });

    public Task<Notification> AddNotificationAsync(Notification n) =>
        WithLockAsync(async () =>
        {
            n.Id = _data.NextNotificationId++;
            _data.Notifications.Add(n);
            await PersistAsync();
            return n;
        });

    public Task<List<Notification>> GetNotificationsAsync(int userId, bool unreadOnly) =>
        WithLockAsync(async () => _data.Notifications.Where(n => n.UserId == userId && (!unreadOnly || !n.Read)).OrderByDescending(n => n.CreatedAt).ToList());

    public Task<Notification?> GetNotificationAsync(int id) =>
        WithLockAsync(async () => _data.Notifications.FirstOrDefault(n => n.Id == id));

    public Task<Notification> UpdateNotificationAsync(Notification n) =>
        WithLockAsync(async () =>
        {
            var idx = _data.Notifications.FindIndex(x => x.Id == n.Id);
            if (idx >= 0) _data.Notifications[idx] = n;
            await PersistAsync();
            return n;
        });

    public Task<List<User>> GetUsersByRoleAsync(string role) =>
        WithLockAsync(async () => _data.Users.Where(u => u.Role == role && u.IsActive).ToList());
    public Task<ApiKey> CreateApiKeyAsync(ApiKey key) =>
        WithLockAsync(async () =>
        {
            key.Id = _data.NextApiKeyId++;
            _data.ApiKeys.Add(key);
            await PersistAsync();
            return key;
        });

    public Task<List<ApiKey>> GetApiKeysAsync() =>
        WithLockAsync(async () => _data.ApiKeys.OrderByDescending(k => k.CreatedAt).ToList());

    public Task<ApiKey?> GetApiKeyByHashAsync(string hash) =>
        WithLockAsync(async () => _data.ApiKeys.FirstOrDefault(k => k.KeyHash == hash));

    public Task<ApiKey?> GetApiKeyAsync(int id) =>
        WithLockAsync(async () => _data.ApiKeys.FirstOrDefault(k => k.Id == id));

    public Task<ApiKey> UpdateApiKeyAsync(ApiKey key) =>
        WithLockAsync(async () =>
        {
            var idx = _data.ApiKeys.FindIndex(k => k.Id == key.Id);
            if (idx >= 0) _data.ApiKeys[idx] = key;
            await PersistAsync();
            return key;
        });

    public Task<List<EnvironmentTarget>> GetEnvironmentTargetsAsync() =>
        WithLockAsync(async () => _data.EnvironmentTargets.OrderBy(e => e.Name).ToList());

    public Task<EnvironmentTarget?> GetEnvironmentTargetAsync(int id) =>
        WithLockAsync(async () => _data.EnvironmentTargets.FirstOrDefault(e => e.Id == id));

    public Task<EnvironmentTarget> CreateEnvironmentTargetAsync(EnvironmentTarget env) =>
        WithLockAsync(async () =>
        {
            env.Id = _data.NextEnvironmentTargetId++;
            _data.EnvironmentTargets.Add(env);
            await PersistAsync();
            return env;
        });

    public Task<EnvironmentTarget> UpdateEnvironmentTargetAsync(EnvironmentTarget env) =>
        WithLockAsync(async () =>
        {
            var idx = _data.EnvironmentTargets.FindIndex(e => e.Id == env.Id);
            if (idx >= 0) _data.EnvironmentTargets[idx] = env;
            await PersistAsync();
            return env;
        });
}
