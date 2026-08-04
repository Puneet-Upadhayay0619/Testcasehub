using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Storage;

public record TestCaseFilter(int? ModuleId, string? Layer, string? VerificationType, string? Status, string? Priority, string? Search);

// Abstraction so the controllers don't care whether data lives in a JSON file or a real
// database — Storage:Mode in appsettings picks the implementation registered in Program.cs.
public interface IDataStore
{
    Task<User?> GetUserByEmailAsync(string email);
    Task<User?> GetUserByIdAsync(int id);
    Task<User> CreateUserAsync(User user);
    Task<List<User>> GetUsersAsync();
    Task<User> UpdateUserAsync(User user);
    Task<int> CountUsersAsync();

    Task<List<Module>> GetModulesAsync();
    Task<Module?> GetModuleAsync(int id);
    Task<bool> ModuleCodeExistsAsync(string code);
    Task<Module> CreateModuleAsync(Module module);
    Task<Dictionary<int, int>> GetTestCaseCountsByModuleAsync();

    Task<TaskLink> CreateTaskLinkAsync(TaskLink link);
    Task<List<TaskLink>> GetTaskLinksAsync(int moduleId);

    Task<List<TestCase>> GetTestCasesAsync(TestCaseFilter filter);
    Task<TestCase?> GetTestCaseAsync(string id);
    Task<int> CountTestCasesWithPrefixAsync(string prefix);
    Task<TestCase> CreateTestCaseAsync(TestCase tc);
    Task<TestCase> UpdateTestCaseAsync(TestCase tc);

    Task AddHistoryAsync(TestCaseHistory history);
    Task<List<TestCaseHistory>> GetHistoryAsync(string testCaseId);

    Task<List<string>> GetPrioritiesAsync();
    Task<bool> PriorityExistsAsync(string value);
    Task AddPriorityAsync(string value);

    Task<List<string>> GetStatusesAsync();
    Task<bool> StatusExistsAsync(string value);
    Task AddStatusAsync(string value);

    // ---- Phase 2: admin user management, invites, audit log ----
    Task AddAuditLogAsync(AuditLog entry);
    Task<List<AuditLog>> GetAuditLogsAsync(int take = 200);

    Task<InviteLink> CreateInviteLinkAsync(InviteLink invite);
    Task<InviteLink?> GetInviteLinkByCodeAsync(string code);
    Task<List<InviteLink>> GetInviteLinksAsync();
    Task<InviteLink> UpdateInviteLinkAsync(InviteLink invite);

    // ---- Phase 3: refresh tokens, password reset ----
    Task<RefreshToken> CreateRefreshTokenAsync(RefreshToken token);
    Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash);
    Task<RefreshToken> UpdateRefreshTokenAsync(RefreshToken token);
    Task RevokeAllRefreshTokensForUserAsync(int userId);

    Task<PasswordResetToken> CreatePasswordResetTokenAsync(PasswordResetToken token);
    Task<PasswordResetToken?> GetPasswordResetTokenByHashAsync(string tokenHash);
    Task<PasswordResetToken> UpdatePasswordResetTokenAsync(PasswordResetToken token);

    // ---- Phase 4: suites, comments ----
    Task<List<TestSuite>> GetSuitesAsync();
    Task<TestSuite?> GetSuiteAsync(int id);
    Task<TestSuite> CreateSuiteAsync(TestSuite suite);
    Task<TestSuite> UpdateSuiteAsync(TestSuite suite);

    Task<List<TestCaseComment>> GetCommentsAsync(string testCaseId);
    Task<TestCaseComment> AddCommentAsync(TestCaseComment comment);
    Task<TestCaseComment?> GetCommentAsync(int id);
    Task<TestCaseComment> UpdateCommentAsync(TestCaseComment comment);

    // ---- Phase 5: releases, test runs, results, notifications ----
    Task<List<Release>> GetReleasesAsync();
    Task<Release?> GetReleaseAsync(int id);
    Task<Release> CreateReleaseAsync(Release release);
    Task<Release> UpdateReleaseAsync(Release release);

    Task<List<TestRun>> GetTestRunsAsync(int? releaseId);
    Task<TestRun?> GetTestRunAsync(int id);
    Task<TestRun> CreateTestRunAsync(TestRun run);

    Task<List<TestRunResult>> GetTestRunResultsAsync(int testRunId);
    Task<TestRunResult?> GetTestRunResultByAttemptKeyAsync(string attemptKey);
    Task<TestRunResult> AddTestRunResultAsync(TestRunResult result);
    Task<TestRunResult> UpdateTestRunResultAsync(TestRunResult result); // narrow use: attaching BugWorkItemId after the fact, not changing the recorded outcome
    Task<List<TestRunResult>> GetResultsForTestCaseAsync(string testCaseId);
    Task<List<TestRunResult>> GetResultsForReleaseAsync(int releaseId);

    Task<Notification> AddNotificationAsync(Notification n);
    Task<List<Notification>> GetNotificationsAsync(int userId, bool unreadOnly);
    Task<Notification?> GetNotificationAsync(int id);
    Task<Notification> UpdateNotificationAsync(Notification n);
    Task<List<User>> GetUsersByRoleAsync(string role);

    // ---- Phase 6: API keys, environment targets ----
    Task<ApiKey> CreateApiKeyAsync(ApiKey key);
    Task<List<ApiKey>> GetApiKeysAsync();
    Task<ApiKey?> GetApiKeyByHashAsync(string hash);
    Task<ApiKey?> GetApiKeyAsync(int id);
    Task<ApiKey> UpdateApiKeyAsync(ApiKey key);

    Task<List<EnvironmentTarget>> GetEnvironmentTargetsAsync();
    Task<EnvironmentTarget?> GetEnvironmentTargetAsync(int id);
    Task<EnvironmentTarget> CreateEnvironmentTargetAsync(EnvironmentTarget env);
    Task<EnvironmentTarget> UpdateEnvironmentTargetAsync(EnvironmentTarget env);
}
