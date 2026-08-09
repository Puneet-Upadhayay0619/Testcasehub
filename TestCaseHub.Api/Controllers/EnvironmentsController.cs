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
    public async Task<ActionResult<List<EnvironmentTargetResponse>>> GetAll()
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, matching Create below.
        return (await _store.GetEnvironmentTargetsAsync()).Select(EnvironmentTargetResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentTargetResponse>> Create(CreateEnvironmentTargetRequest req)
    {
        if (!User.CanManageUsers()) return Forbid(); // Admin-only, same as secrets/user management
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        if (!Models.EnvironmentType.All.Contains(req.EnvironmentType)) return BadRequest("EnvironmentType must be Staging or Production.");

        var env = new EnvironmentTarget
        {
            Name = req.Name.Trim(), Tenant = req.Tenant ?? "", EnvironmentType = req.EnvironmentType,
            DashboardBaseUrl = req.DashboardBaseUrl ?? "", AppApiBaseUrl = req.AppApiBaseUrl ?? "", AppBaseUrl = req.AppBaseUrl ?? "",
            RequiresTestDataCleanup = req.RequiresTestDataCleanup, CreatedBy = ActorDisplayName
        };
        if (!string.IsNullOrWhiteSpace(req.MasterDbConnectionString)) env.MasterDbConnectionStringEncrypted = _protector.Protect(req.MasterDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.TransactionDbConnectionString)) env.TransactionDbConnectionStringEncrypted = _protector.Protect(req.TransactionDbConnectionString);
        if (!string.IsNullOrWhiteSpace(req.ReportDbConnectionString)) env.ReportDbConnectionStringEncrypted = _protector.Protect(req.ReportDbConnectionString);

        env = await _store.CreateEnvironmentTargetAsync(env);
        return EnvironmentTargetResponse.From(env);
    }
}
