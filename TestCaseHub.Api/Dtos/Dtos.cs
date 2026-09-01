using System.Text.Json.Serialization;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Dtos;

public record RegisterRequest(string Email, string Password, string DisplayName, string? InviteCode = null);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, string Email, string DisplayName, string Role, string RefreshToken, int? CompanyId = null, List<int>? TeamIds = null, string? CompanyName = null);
public record RefreshRequest(string RefreshToken);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);

public record ModuleCreateRequest(string Name, string Code, string Description, string Owner, string Status);
public record ModuleResponse(int Id, string Name, string Code, string Description, string Owner, string Status, DateTime CreatedAt, int TestCaseCount, int CompanyId = 0, List<int>? TeamIds = null);

public record TaskLinkCreateRequest(string Layer, string AdoProject, string AdoTaskId, string AdoTaskTitle, string AdoTaskUrl);
public record TaskLinkResponse(int Id, int ModuleId, string Layer, string AdoProject, string AdoTaskId, string AdoTaskTitle, string AdoTaskUrl, DateTime LinkedAt);

public record TestCaseStepDto(
    [property: JsonPropertyName("step_no")] int StepNo,
    string Action,
    [property: JsonPropertyName("expected_result")] string ExpectedResult
);

public record AutomationConfigDto(string? ApiEndpoint, string? ApiMethod, int? ApiExpectedStatus, string? DbQuery, string? DbExpectedValue);

public record TestCaseCreateRequest(
    int ModuleId, string Layer, string VerificationType, string Title, string Preconditions,
    List<TestCaseStepDto> Steps, string Priority, string Type, string Status,
    List<string> Tags, bool AutomationReady, string AutomationScriptRef,
    string? HistoryComment = null, List<int>? LinkedModuleIds = null,
    AutomationConfigDto? AutomationConfig = null, string? SelectorStability = null,
    int? TeamId = null
);

public record TestCaseResponse(
    string Id, int ModuleId, string Layer, string VerificationType, string Title, string Preconditions,
    List<TestCaseStepDto> Steps, string Priority, string Type, string Status,
    List<string> Tags, bool AutomationReady, string AutomationScriptRef,
    string CreatedBy, DateTime CreatedAt, string UpdatedBy, DateTime UpdatedAt, int Version,
    List<int> LinkedModuleIds, AutomationConfigDto? AutomationConfig, string SelectorStability,
    int? TeamId
)
{
    public static TestCaseResponse From(TestCase tc)
    {
        AutomationConfigDto? cfg = null;
        try { cfg = System.Text.Json.JsonSerializer.Deserialize<AutomationConfigDto>(tc.AutomationConfigJson); } catch { }
        return new(
            tc.Id, tc.ModuleId, tc.Layer, tc.VerificationType, tc.Title, tc.Preconditions,
            tc.Steps.Select(s => new TestCaseStepDto(s.StepNo, s.Action, s.ExpectedResult)).ToList(),
            tc.Priority, tc.Type, tc.Status, tc.Tags, tc.AutomationReady, tc.AutomationScriptRef,
            tc.CreatedBy, tc.CreatedAt, tc.UpdatedBy, tc.UpdatedAt, tc.Version, tc.LinkedModuleIds,
            cfg, tc.SelectorStability, tc.TeamId
        );
    }
}

public record HistoryResponse(int Id, string TestCaseId, string ChangedBy, DateTime ChangedAt, string ChangeType, string? OldSnapshotJson, string NewSnapshotJson, string Comment);

public record AddLookupRequest(string Value);

public record UserResponse(int Id, string Email, string DisplayName, string Role, bool IsActive, List<string> LayerScope, List<int> ModuleScope, DateTime CreatedAt, int? CompanyId, List<int>? TeamIds = null)
{
    public static UserResponse From(TestCaseHub.Api.Models.User u, List<int>? teamIds = null) =>
        new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.LayerScope, u.ModuleScope, u.CreatedAt, u.CompanyId, teamIds);
}

public record UpdateUserAccessRequest(string Role, List<string>? LayerScope, List<int>? ModuleScope);
public record UpdateEmailRequest(string NewEmail);
public record AssignCompanyAdminRequest(string Email);

public record AuditLogResponse(int Id, string ActorEmail, string ActorDisplayName, string Action, string TargetDescription, string DetailsJson, DateTime OccurredAt);

public record CreateInviteRequest(int MaxUses, int ExpiresInDays);
public record InviteLinkResponse(int Id, string Code, int MaxUses, int UsedCount, DateTime ExpiresAt, bool Revoked, string CreatedByEmail, DateTime CreatedAt, bool IsUsable)
{
    public static InviteLinkResponse From(TestCaseHub.Api.Models.InviteLink i) =>
        new(i.Id, i.Code, i.MaxUses, i.UsedCount, i.ExpiresAt, i.Revoked, i.CreatedByEmail, i.CreatedAt, i.IsUsable);
}

public record CreateStaticSuiteRequest(string Name, string Description, List<string> TestCaseIds);
public record CreateDynamicSuiteRequest(string Name, string Description, int? ModuleId, string? Layer, string? VerificationType, string? Status, string? Priority, string? Tag, string? Search);
public record SuiteResponse(int Id, string Name, string Description, string Kind, List<string> TestCaseIds, string FilterJson, string CreatedBy, DateTime CreatedAt)
{
    public static SuiteResponse From(TestCaseHub.Api.Models.TestSuite s) =>
        new(s.Id, s.Name, s.Description, s.Kind, s.TestCaseIds, s.FilterJson, s.CreatedBy, s.CreatedAt);
}

public record BulkEditRequest(List<string> Ids, string? Priority, string? Status, List<string>? AddTags, List<string>? RemoveTags, string? HistoryComment);
public record BulkEditResult(List<string> Updated, List<string> NotFound);
public record BulkDeleteRequest(List<string> Ids);
public record BulkDeleteResult(List<string> Deleted, List<string> NotFound, List<string> Forbidden);

public record HistoryDiffEntry(string Field, string? OldValue, string? NewValue);

public record AddCommentRequest(string Body);
public record CommentResponse(int Id, string TestCaseId, string AuthorEmail, string AuthorDisplayName, string Body, DateTime CreatedAt, bool Deleted)
{
    public static CommentResponse From(TestCaseHub.Api.Models.TestCaseComment c) =>
        new(c.Id, c.TestCaseId, c.AuthorEmail, c.AuthorDisplayName, c.Deleted ? "[deleted by moderator]" : c.Body, c.CreatedAt, c.Deleted);
}

public record DuplicateGroup(string ReasonKey, List<TestCaseResponse> Cases);

public record CreateReleaseRequest(string Name, string Version);
public record ReleaseResponse(int Id, string Name, string Version, string Status, string CreatedBy, DateTime CreatedAt, string? ApprovedBy, DateTime? ApprovedAt, string ApprovalComment)
{
    public static ReleaseResponse From(TestCaseHub.Api.Models.Release r) =>
        new(r.Id, r.Name, r.Version, r.Status, r.CreatedBy, r.CreatedAt, r.ApprovedBy, r.ApprovedAt, r.ApprovalComment);
}
public record TransitionReleaseRequest(string NewStatus, string? Comment);

public record CreateTestRunRequest(int? ReleaseId, int? SuiteId, string Name, string TargetEnvironment, int? EnvironmentTargetId = null, int? EnvironmentCredentialId = null);
public record TestRunResponse(int Id, int? ReleaseId, int? SuiteId, string Name, string TargetEnvironment, int? EnvironmentTargetId, int? EnvironmentCredentialId, string CreatedBy, DateTime CreatedAt)
{
    public static TestRunResponse From(TestCaseHub.Api.Models.TestRun t) =>
        new(t.Id, t.ReleaseId, t.SuiteId, t.Name, t.TargetEnvironment, t.EnvironmentTargetId, t.EnvironmentCredentialId, t.CreatedBy, t.CreatedAt);
}

public record RecordManualResultRequest(string TestCaseId, string? Platform, string Status, string? Notes);
public record TestRunResultResponse(int Id, int TestRunId, string TestCaseId, string? Platform, string Status, bool IsAutomated, string ExecutedBy, DateTime ExecutedAt, string Notes, int RetryCount, string? BugWorkItemId)
{
    public static TestRunResultResponse From(TestCaseHub.Api.Models.TestRunResult r) =>
        new(r.Id, r.TestRunId, r.TestCaseId, r.Platform, r.Status, r.IsAutomated, r.ExecutedBy, r.ExecutedAt, r.Notes, r.RetryCount, r.BugWorkItemId);
}

public record TestRunRollup(int TotalCases, int Passed, int Failed, int Blocked, int Skipped, int NotRun, double PassRatePercent);

public record CoverageRow(int ModuleId, string ModuleName, int TotalCases, int CasesWithAtLeastOnePass, double CoveragePercent);

public record ReleaseTrendPoint(int ReleaseId, string ReleaseName, double PassRatePercent, int TotalResults);

public record NotificationResponse(int Id, string Type, string Message, bool Read, DateTime CreatedAt);

public record CreateApiKeyRequest(string Name, string? Scope);
public record ApiKeyResponse(int Id, string Name, string Scope, bool Revoked, string CreatedBy, DateTime CreatedAt, DateTime? LastUsedAt)
{
    public static ApiKeyResponse From(TestCaseHub.Api.Models.ApiKey k) => new(k.Id, k.Name, k.Scope, k.Revoked, k.CreatedBy, k.CreatedAt, k.LastUsedAt);
}
public record IssuedApiKeyResponse(int Id, string Name, string RawKey);

public record CreateEnvironmentTargetRequest(string Name, string Tenant, string EnvironmentType, string DashboardBaseUrl, string AppApiBaseUrl, string AppBaseUrl, string? MasterDbConnectionString, string? TransactionDbConnectionString, string? ReportDbConnectionString, bool RequiresTestDataCleanup);
public record EnvironmentTargetResponse(int Id, string Name, string Tenant, string EnvironmentType, string DashboardBaseUrl, string AppApiBaseUrl, string AppBaseUrl, bool HasMasterDbConnection, bool HasTransactionDbConnection, bool HasReportDbConnection, bool RequiresTestDataCleanup, int? TestCompanyId, int? TestCompanyBId, int? TestReservedModuleEnum, bool AllowDestructiveTestSql, string CreatedBy, DateTime CreatedAt)
{
    public static EnvironmentTargetResponse From(TestCaseHub.Api.Models.EnvironmentTarget e) => new(
        e.Id, e.Name, e.Tenant, e.EnvironmentType, e.DashboardBaseUrl, e.AppApiBaseUrl, e.AppBaseUrl,
        !string.IsNullOrEmpty(e.MasterDbConnectionStringEncrypted), !string.IsNullOrEmpty(e.TransactionDbConnectionStringEncrypted), !string.IsNullOrEmpty(e.ReportDbConnectionStringEncrypted),
        e.RequiresTestDataCleanup, e.TestCompanyId, e.TestCompanyBId, e.TestReservedModuleEnum, e.AllowDestructiveTestSql, e.CreatedBy, e.CreatedAt
    );
}
// Sets the real FieldAssist tenant CompanyId(s)/reserved test module used to scope native
// execution's SQL assertions -- separate from this Test Case Hub's own CompanyId, which lives in
// a totally different numbering space (see EnvironmentTarget.TestCompanyId and friends). All
// three optional so a caller can set just one without clobbering the others.
public record SetTestCompanyIdRequest(int? TestCompanyId, int? TestCompanyBId, int? TestReservedModuleEnum, bool? AllowDestructiveTestSql);

public record RecordAutomatedResultRequest(string TestCaseId, string? Platform, string Status, string? Notes, string RunAttemptKey, int RetryCount);
public record CreateBugFromResultResponse(bool Success, string? WorkItemId, string? WorkItemUrl, string? Error);

// ---- Automation-generation architecture: repo links, per-environment credentials, generated
// scripts (agreed in planning) ----

public record CreateModuleRepoLinkRequest(string RepoHost, string Layer, string OrgOrAccount, string? Project, string RepoName, string? Branch, string? BasePath, string? AccessToken);
public record ModuleRepoLinkResponse(int Id, int ModuleId, string RepoHost, string Layer, string OrgOrAccount, string Project, string RepoName, string Branch, string BasePath, bool HasAccessToken, string CreatedBy, DateTime CreatedAt, string UpdatedBy, DateTime? UpdatedAt)
{
    public static ModuleRepoLinkResponse From(TestCaseHub.Api.Models.ModuleRepoLink l) => new(
        l.Id, l.ModuleId, l.RepoHost, l.Layer, l.OrgOrAccount, l.Project, l.RepoName, l.Branch, l.BasePath,
        !string.IsNullOrEmpty(l.AccessTokenEncrypted), l.CreatedBy, l.CreatedAt, l.UpdatedBy, l.UpdatedAt
    );
}

public record CreateEnvironmentCredentialRequest(string Label, string? Email, string Password, string? Tag);
public record EnvironmentCredentialResponse(int Id, int EnvironmentTargetId, string Label, string Email, string Tag, bool HasPassword, string CreatedBy, DateTime CreatedAt, string UpdatedBy, DateTime? UpdatedAt)
{
    public static EnvironmentCredentialResponse From(TestCaseHub.Api.Models.EnvironmentCredential c) => new(
        c.Id, c.EnvironmentTargetId, c.Label, c.Email, c.Tag, !string.IsNullOrEmpty(c.PasswordEncrypted), c.CreatedBy, c.CreatedAt, c.UpdatedBy, c.UpdatedAt
    );
}


public record SaveCompanyAiSettingsRequest(string? Provider, string? Model, string? ApiKey, bool Enabled);
public record CompanyAiSettingsResponse(int CompanyId, string Provider, string Model, bool Enabled, bool HasApiKey, string CreatedBy, DateTime CreatedAt, string UpdatedBy, DateTime? UpdatedAt)
{
    public static CompanyAiSettingsResponse From(TestCaseHub.Api.Models.CompanyAiSettings a) => new(
        a.CompanyId, a.Provider, a.Model, a.Enabled, !string.IsNullOrEmpty(a.ApiKeyEncrypted), a.CreatedBy, a.CreatedAt, a.UpdatedBy, a.UpdatedAt
    );
}
public record GenerateAutomationScriptRequest(int ModuleId, string TestCaseId, string? Framework);

// Batch generation, scoped to Module + Layer so each test case is grounded against the repo
// link for the layer it actually belongs to (UI test -> Frontend link, API test -> Backend
// link, DB test -> Database link) -- capped at 5 test cases per call. The cap exists because
// each item is still its own full Anthropic call under the hood (same reliability reasoning as
// single-generate: bounded prompt size, one failure doesn't take down 40 others, cost stays
// visible per batch) -- this just removes the need to click Generate 46 times by hand.
// Layer = product area (Dashboard/App-API/App) -- useful for a module that spans multiple
// areas. VerificationType = kind of test (UI/API-Contract/Database) -- this is what "UWMC ka
// UI Layer / API Layer / Database Layer" actually maps to for a Dashboard-only module where
// every test case has the same Layer. Both optional, both AND'ed when given.
public record GenerateAutomationScriptsBatchRequest(int ModuleId, string? Layer, string? VerificationType, List<string> TestCaseIds, string? Framework);
public record BatchGenerationItemResult(string TestCaseId, bool Success, string? Error, AutomationScriptResponse? Script, List<string> Warnings);
public record BatchGenerationResponse(int Requested, int Succeeded, int Failed, List<BatchGenerationItemResult> Items);

public record SaveAutomationScriptRequest(int ModuleId, string? TestCaseId, int? SuiteId, string FileName, string? Framework, string Content, string? GeneratedBy, string? SourceRepoRefs);
public record UpdateAutomationScriptStatusRequest(string Status);
public record AutomationScriptResponse(int Id, int CompanyId, int ModuleId, string? TestCaseId, int? SuiteId, string FileName, string Framework, string Content, string Status, string GeneratedBy, DateTime GeneratedAt, int Version, string SourceRepoRefs, bool HasExecutionDefinition, string TestTier)
{
    public static AutomationScriptResponse From(TestCaseHub.Api.Models.AutomationScript s) => new(
        s.Id, s.CompanyId, s.ModuleId, s.TestCaseId, s.SuiteId, s.FileName, s.Framework, s.Content, s.Status, s.GeneratedBy, s.GeneratedAt, s.Version, s.SourceRepoRefs,
        !string.IsNullOrWhiteSpace(s.ExecutionDefinitionJson), s.TestTier
    );
}
public record UpdateAutomationScriptTierRequest(string TestTier);

// Native execution (agreed: "test case hub se hi complete testing") -- set once per script by
// whoever converts its Playwright logic into the step DSL, then re-used by every future Run.
public record SetExecutionDefinitionRequest(string ExecutionDefinitionJson);

// Run a script natively (no external tooling) against a configured EnvironmentTarget, optionally
// authenticating as a named EnvironmentCredential, and optionally recording the outcome straight
// into a TestRun -- the same TestRunResult row a human's "Record result" or a CI's
// results/automated call would create, just posted by the interpreter itself.
public record ExecuteAutomationScriptRequest(int EnvironmentTargetId, int? EnvironmentCredentialId, int? TestRunId, string? Mode);
public record ExecuteAutomationScriptResponse(bool Passed, string Status, List<string> Log, string? Error, int? TestRunResultId);

// Batch "Run tier" -- runs every Approved script in a Module+TestTier that has a native
// execution definition, one after another, against the same EnvironmentTarget/credential/mode.
// Mirrors the single-script Execute contract but returns one outcome per script plus a summary.
public record ExecuteBatchRequest(int ModuleId, string TestTier, int EnvironmentTargetId, int? EnvironmentCredentialId, int? TestRunId, string? Mode);
public record ExecuteBatchItemResult(int ScriptId, string? TestCaseId, string FileName, bool Passed, string Status, string? Error, int? TestRunResultId);
public record ExecuteBatchResponse(int Total, int Passed, int Failed, int Skipped, List<ExecuteBatchItemResult> Items);

// ---- Phase 8: multi-company, teams ----
public record UpdateCompanyStatusRequest(string Status);

public record CompanyResponse(int Id, string Name, string Status, string CreatedBy, DateTime CreatedAt)
{
    public static CompanyResponse From(TestCaseHub.Api.Models.Company c) => new(c.Id, c.Name, c.Status, c.CreatedBy, c.CreatedAt);
}
public record CreateCompanyRequest(string Name);

public record CreateCompanyAdminInviteRequest(int CompanyId, int MaxUses = 1, int ExpiresInDays = 14);
public record CompanyAdminInviteResponse(int Id, int CompanyId, string Code, int MaxUses, int UsedCount, DateTime ExpiresAt, bool Revoked, string CreatedByEmail, DateTime CreatedAt, bool IsUsable)
{
    public static CompanyAdminInviteResponse From(TestCaseHub.Api.Models.CompanyAdminInvite i) =>
        new(i.Id, i.CompanyId, i.Code, i.MaxUses, i.UsedCount, i.ExpiresAt, i.Revoked, i.CreatedByEmail, i.CreatedAt, i.IsUsable);
}

public record CreateTeamRequest(string Name, string? Description, int? CompanyId = null);
public record TeamResponse(int Id, int CompanyId, string Name, string Description, string CreatedBy, DateTime CreatedAt, List<int> MemberUserIds, List<int> ModuleIds)
{
    public static TeamResponse From(TestCaseHub.Api.Models.Team t, List<int> memberUserIds, List<int> moduleIds) =>
        new(t.Id, t.CompanyId, t.Name, t.Description, t.CreatedBy, t.CreatedAt, memberUserIds, moduleIds);
}
public record TeamMemberRequest(int UserId);
public record TeamModuleRequest(int ModuleId);

public record AssignUsersByDomainRequest(string EmailDomain);
public record AssignUsersByDomainResult(int MatchedCount, List<string> Emails);
