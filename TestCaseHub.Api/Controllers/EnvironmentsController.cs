using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Admin-only CRUD for the multi-tenant/multi-environment targets agreed in planning: per-layer
// base URLs + the three-part DB architecture (MasterDB/TransactionDB/ReportDB), one row per
// tenant+environment combination. Connection strings are encrypted at rest (SecretProtector)
// and NEVER returned in full — GET responses only say whether one is configured.
[ApiController]
[Authorize]
[Route("api/environments")]
public class EnvironmentsController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly SecretProtector _protector;
    public EnvironmentsController(IDataStore store, SecretProtector protector) { _store = store; _protector = protector; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<EnvironmentTargetResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, matching Create below.
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        return (await _store.GetEnvironmentTargetsAsync()).Where(e => e.CompanyId == effective).Select(EnvironmentTargetResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentTargetResponse>> Create(CreateEnvironmentTargetRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, same as secrets/user management
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        if (!Models.EnvironmentType.All.Contains(req.EnvironmentType)) return BadRequest("EnvironmentType must be Staging or Production.");
        var testingPlatform = string.IsNullOrWhiteSpace(req.TestingPlatform) ? Models.TestingPlatform.Dashboard : req.TestingPlatform;
        if (!Models.TestingPlatform.Core3.Contains(testingPlatform)) return BadRequest("TestingPlatform must be Dashboard, App-API, or App -- an environment is one concrete piece of infrastructure, not a combined view.");

        var env = new EnvironmentTarget
        {
            CompanyId = companyId.Value,
            Name = req.Name.Trim(), Tenant = req.Tenant ?? "", EnvironmentType = req.EnvironmentType,
            DashboardBaseUrl = req.DashboardBaseUrl ?? "", AppApiBaseUrl = req.AppApiBaseUrl ?? "", AppBaseUrl = req.AppBaseUrl ?? "",
            RequiresTestDataCleanup = req.RequiresTestDataCleanup, TestingPlatform = testingPlatform, CreatedBy = ActorDisplayName
        };
        if (!string.IsNullOrWhiteSpace(req.MasterDbConnectionString)) env.MasterDbConnectionStringEncrypted = _protector.Protect(req.MasterDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.TransactionDbConnectionString)) env.TransactionDbConnectionStringEncrypted = _protector.Protect(req.TransactionDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.ReportDbConnectionString)) env.ReportDbConnectionStringEncrypted = _protector.Protect(req.ReportDbConnectionString);

        env = await _store.CreateEnvironmentTargetAsync(env);
        return EnvironmentTargetResponse.From(env);
    }

    // Full edit -- Name/Tenant/Type/URLs/cleanup-flag always overwritten; DB connection strings
    // only rotated if a non-blank value was actually supplied (same "blank = leave unchanged"
    // convention as UpdateCredential below), since GET responses never return the plaintext to
    // pre-fill an edit form with in the first place.
    [HttpPut("{id:int}")]
    public async Task<ActionResult<EnvironmentTargetResponse>> Update(int id, CreateEnvironmentTargetRequest req)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, same bar as Create/Delete.
        var env = await _store.GetEnvironmentTargetAsync(id);
        if (env is null) return NotFound("Environment target not found.");
        if (!User.HasCompanyAccess(env.CompanyId)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        if (!Models.EnvironmentType.All.Contains(req.EnvironmentType)) return BadRequest("EnvironmentType must be Staging or Production.");
        var testingPlatform = string.IsNullOrWhiteSpace(req.TestingPlatform) ? env.TestingPlatform : req.TestingPlatform;
        if (!Models.TestingPlatform.Core3.Contains(testingPlatform)) return BadRequest("TestingPlatform must be Dashboard, App-API, or App -- an environment is one concrete piece of infrastructure, not a combined view.");

        env.Name = req.Name.Trim(); env.Tenant = req.Tenant ?? ""; env.EnvironmentType = req.EnvironmentType;
        env.DashboardBaseUrl = req.DashboardBaseUrl ?? ""; env.AppApiBaseUrl = req.AppApiBaseUrl ?? ""; env.AppBaseUrl = req.AppBaseUrl ?? "";
        env.RequiresTestDataCleanup = req.RequiresTestDataCleanup; env.TestingPlatform = testingPlatform;
        if (!string.IsNullOrWhiteSpace(req.MasterDbConnectionString)) env.MasterDbConnectionStringEncrypted = _protector.Protect(req.MasterDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.TransactionDbConnectionString)) env.TransactionDbConnectionStringEncrypted = _protector.Protect(req.TransactionDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.ReportDbConnectionString)) env.ReportDbConnectionStringEncrypted = _protector.Protect(req.ReportDbConnectionString);

        env = await _store.UpdateEnvironmentTargetAsync(env);
        return EnvironmentTargetResponse.From(env);
    }

    // Cascades to this environment's named execution credentials (same "delete the children
    // first" convention as DeleteModuleAsync elsewhere in this codebase) -- a dangling
    // EnvironmentCredential pointing at a deleted EnvironmentTargetId would be unreachable
    // through the UI anyway, so removing it outright is simpler than orphaning it.
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, same bar as Create/Update.
        var env = await _store.GetEnvironmentTargetAsync(id);
        if (env is null) return NotFound("Environment target not found.");
        if (!User.HasCompanyAccess(env.CompanyId)) return Forbid();

        await _store.DeleteEnvironmentTargetAsync(id);
        return NoContent();
    }

    // Separate small endpoint rather than folding into a general Update (none exists yet for
    // EnvironmentTarget) -- this is the one field native execution actually needs post-creation,
    // and keeping it narrow avoids having to design a full PUT contract for every other field
    // (base URLs, DB connection strings) that isn't needed for this feature.
    [HttpPatch("{id:int}/test-company-id")]
    public async Task<ActionResult<EnvironmentTargetResponse>> SetTestCompanyId(int id, SetTestCompanyIdRequest req)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, same bar as everything else on this entity
        var env = await _store.GetEnvironmentTargetAsync(id);
        if (env is null) return NotFound("Environment target not found.");
        if (!User.HasCompanyAccess(env.CompanyId)) return Forbid();
        if (req.TestCompanyId is not null) env.TestCompanyId = req.TestCompanyId;
        if (req.TestCompanyBId is not null) env.TestCompanyBId = req.TestCompanyBId;
        if (req.TestReservedModuleEnum is not null) env.TestReservedModuleEnum = req.TestReservedModuleEnum;
        if (req.AllowDestructiveTestSql is not null) env.AllowDestructiveTestSql = req.AllowDestructiveTestSql.Value;
        env = await _store.UpdateEnvironmentTargetAsync(env);
        return EnvironmentTargetResponse.From(env);
    }

    // --- Named execution credentials (agreed in planning): an environment often needs several
    // distinct logins (a plain admin, a non-Modern-Trade company's admin, a second company for
    // isolation checks) -- exactly what the UWMC automation project itself needed. Configuring
    // one is Admin-only (CanConfigureAutomationCredentials); TRIGGERING a run that uses one
    // only requires CanTriggerTestRun (Lead+, see TestRunsController) -- a Lead picks by Label,
    // never sees the plaintext password. ---

    [HttpGet("{environmentTargetId:int}/credentials")]
    public async Task<ActionResult<List<EnvironmentCredentialResponse>>> GetCredentials(int environmentTargetId)
    {
        var env = await _store.GetEnvironmentTargetAsync(environmentTargetId);
        if (env is null) return NotFound("Environment target not found.");
        if (!User.HasCompanyAccess(env.CompanyId)) return Forbid();
        // Anyone who can at least trigger a run needs to see the list of Labels to pick from --
        // the response never includes the password itself (see EnvironmentCredentialResponse).
        if (!User.CanTriggerTestRun()) return Forbid();

        var creds = await _store.GetEnvironmentCredentialsAsync(environmentTargetId);
        return creds.Select(EnvironmentCredentialResponse.From).ToList();
    }

    [HttpPost("{environmentTargetId:int}/credentials")]
    public async Task<ActionResult<EnvironmentCredentialResponse>> CreateCredential(int environmentTargetId, CreateEnvironmentCredentialRequest req)
    {
        if (!User.CanConfigureAutomationCredentials()) return Forbid();

        var env = await _store.GetEnvironmentTargetAsync(environmentTargetId);
        if (env is null) return NotFound("Environment target not found.");
        if (!User.HasCompanyAccess(env.CompanyId)) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("Label is required.");
        if (string.IsNullOrWhiteSpace(req.Password)) return BadRequest("Password is required.");

        var cred = new EnvironmentCredential
        {
            EnvironmentTargetId = environmentTargetId, Label = req.Label.Trim(),
            Email = req.Email?.Trim() ?? "", Tag = req.Tag?.Trim() ?? "",
            PasswordEncrypted = _protector.Protect(req.Password), CreatedBy = ActorDisplayName
        };
        cred = await _store.CreateEnvironmentCredentialAsync(cred);
        return Ok(EnvironmentCredentialResponse.From(cred));
    }

    [HttpPut("{environmentTargetId:int}/credentials/{id:int}")]
    public async Task<ActionResult<EnvironmentCredentialResponse>> UpdateCredential(int environmentTargetId, int id, CreateEnvironmentCredentialRequest req)
    {
        if (!User.CanConfigureAutomationCredentials()) return Forbid();

        var cred = await _store.GetEnvironmentCredentialAsync(id);
        if (cred is null || cred.EnvironmentTargetId != environmentTargetId) return NotFound("Credential not found.");
        var env = await _store.GetEnvironmentTargetAsync(environmentTargetId);
        if (env is null || !User.HasCompanyAccess(env.CompanyId)) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("Label is required.");
        cred.Label = req.Label.Trim(); cred.Email = req.Email?.Trim() ?? ""; cred.Tag = req.Tag?.Trim() ?? "";
        // Only rotate the password if a new one was actually supplied.
        if (!string.IsNullOrWhiteSpace(req.Password)) cred.PasswordEncrypted = _protector.Protect(req.Password);
        cred.UpdatedBy = ActorDisplayName; cred.UpdatedAt = DateTime.UtcNow;

        cred = await _store.UpdateEnvironmentCredentialAsync(cred);
        return Ok(EnvironmentCredentialResponse.From(cred));
    }

    [HttpDelete("{environmentTargetId:int}/credentials/{id:int}")]
    public async Task<ActionResult> DeleteCredential(int environmentTargetId, int id)
    {
        if (!User.CanConfigureAutomationCredentials()) return Forbid();

        var cred = await _store.GetEnvironmentCredentialAsync(id);
        if (cred is null || cred.EnvironmentTargetId != environmentTargetId) return NotFound("Credential not found.");
        var env = await _store.GetEnvironmentTargetAsync(environmentTargetId);
        if (env is null || !User.HasCompanyAccess(env.CompanyId)) return Forbid();

        await _store.DeleteEnvironmentCredentialAsync(id);
        return NoContent();
    }
}
