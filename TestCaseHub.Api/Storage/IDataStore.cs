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
    Task<bool> ModuleCodeExistsAsync(int companyId, string code);
    Task<Module> CreateModuleAsync(Module module);
    Task<Module> UpdateModuleAsync(Module module); // narrow use: Phase 8 company backfill only -- modules have no general edit UI
    // Permanently deletes a module AND everything that only makes sense in the context of that
    // module: its test cases, its task links, and its team-module assignment links. History/
    // Comment/TestRunResult rows for those test cases are deliberately left in place (same
    // audit-trail tradeoff as DeleteTestCaseAsync -- no formal FK to violate, and it preserves
    // "this existed, here's its full history" even after the module itself is gone). No-op if
    // the module doesn't exist.
    Task DeleteModuleAsync(int moduleId);
    Task<Dictionary<int, int>> GetTestCaseCountsByModuleAsync();

    Task<TaskLink> CreateTaskLinkAsync(TaskLink link);
    Task<List<TaskLink>> GetTaskLinksAsync(int moduleId);

    Task<List<TestCase>> GetTestCasesAsync(TestCaseFilter filter);
    Task<TestCase?> GetTestCaseAsync(string id);
    Task<int> CountTestCasesWithPrefixAsync(string prefix);
    Task<TestCase> CreateTestCaseAsync(TestCase tc);
    Task<TestCase> UpdateTestCaseAsync(TestCase tc);
    Task DeleteTestCaseAsync(string id);

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
    Task<TestRun> UpdateTestRunAsync(TestRun run); // narrow use: Phase 8 company backfill only

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

    // ---- Phase 8: multi-company, teams ----
    Task<Company> CreateCompanyAsync(Company company);
    Task<List<Company>> GetCompaniesAsync();
    Task<Company?> GetCompanyAsync(int id);
    Task<Company> UpdateCompanyAsync(Company company);

    Task<CompanyAdminInvite> CreateCompanyAdminInviteAsync(CompanyAdminInvite invite);
    Task<CompanyAdminInvite?> GetCompanyAdminInviteByCodeAsync(string code);
    Task<List<CompanyAdminInvite>> GetCompanyAdminInvitesAsync(int? companyId);
    Task<CompanyAdminInvite> UpdateCompanyAdminInviteAsync(CompanyAdminInvite invite);

    Task<Team> CreateTeamAsync(Team team);
    Task<List<Team>> GetTeamsAsync(int companyId);
    Task<List<Team>> GetAllTeamsAsync();
    Task<Team?> GetTeamAsync(int id);
    Task<Team> UpdateTeamAsync(Team team);

    Task AddTeamMemberAsync(int teamId, int userId);
    Task RemoveTeamMemberAsync(int teamId, int userId);
    Task<List<User>> GetTeamMembersAsync(int teamId);
    Task<List<int>> GetTeamIdsForUserAsync(int userId);

    Task AddTeamModuleAsync(int teamId, int moduleId);
    Task RemoveTeamModuleAsync(int teamId, int moduleId);
    Task<List<int>> GetModuleIdsForTeamAsync(int teamId);
    Task<List<int>> GetTeamIdsForModuleAsync(int moduleId);
}
