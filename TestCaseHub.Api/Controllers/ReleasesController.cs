using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/releases")]
public class ReleasesController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly NotificationService _notify;
    public ReleasesController(IDataStore store, NotificationService notify) { _store = store; _notify = notify; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    // Allowed forward moves. Draft/InTesting/ReadyForSignoff are Contributor+ (day-to-day
    // progress); only Approved/Rejected — the actual "release status" decision — require
    // Lead/Admin, matching the agreed sign-off rule.
    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        [ReleaseStatus.Draft] = new[] { ReleaseStatus.InTesting },
        [ReleaseStatus.InTesting] = new[] { ReleaseStatus.ReadyForSignoff, ReleaseStatus.Draft },
        [ReleaseStatus.ReadyForSignoff] = new[] { ReleaseStatus.Approved, ReleaseStatus.Rejected, ReleaseStatus.InTesting },
        [ReleaseStatus.Rejected] = new[] { ReleaseStatus.InTesting },
        [ReleaseStatus.Approved] = Array.Empty<string>() // terminal — approved releases aren't reopened
    };

    [HttpGet]
    public async Task<ActionResult<List<ReleaseResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        return (await _store.GetReleasesAsync()).Where(r => r.CompanyId == effective).Select(ReleaseResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ReleaseResponse>> GetOne(int id)
    {
        var r = await _store.GetReleaseAsync(id);
        if (r is null) return NotFound();
        if (!User.HasCompanyAccess(r.CompanyId)) return Forbid();
        return ReleaseResponse.From(r);
    }

    [HttpPost]
    public async Task<ActionResult<ReleaseResponse>> Create(CreateReleaseRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.IsAtLeast(Roles.Contributor)) return Forbid();
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Release name is required.");
        var release = new Release { CompanyId = companyId.Value, Name = req.Name.Trim(), Version = req.Version ?? "", CreatedBy = ActorDisplayName };
        release = await _store.CreateReleaseAsync(release);
        return ReleaseResponse.From(release);
    }

    [HttpPost("{id:int}/transition")]
    public async Task<ActionResult<ReleaseResponse>> Transition(int id, TransitionReleaseRequest req)
    {
        var release = await _store.GetReleaseAsync(id);
        if (release is null) return NotFound();
        if (!User.HasCompanyAccess(release.CompanyId)) return Forbid();
        if (!ReleaseStatus.All.Contains(req.NewStatus)) return BadRequest("Unknown status.");

        if (!AllowedTransitions.TryGetValue(release.Status, out var allowed) || !allowed.Contains(req.NewStatus))
            return BadRequest($"Cannot move a release from {release.Status} to {req.NewStatus}.");

        var isSignoffDecision = req.NewStatus is ReleaseStatus.Approved or ReleaseStatus.Rejected;
        if (isSignoffDecision)
        {
            if (!User.IsAtLeast(Roles.Lead)) return Forbid();
            if (string.IsNullOrWhiteSpace(req.Comment))
                return BadRequest("A comment is required when approving or rejecting a release.");
        }
        else if (!User.IsAtLeast(Roles.Contributor)) return Forbid();

        release.Status = req.NewStatus;
        if (isSignoffDecision)
        {
            release.ApprovedBy = ActorDisplayName;
            release.ApprovedAt = DateTime.UtcNow;
            release.ApprovalComment = req.Comment ?? "";
        }
        release = await _store.UpdateReleaseAsync(release);

        if (req.NewStatus == ReleaseStatus.ReadyForSignoff)
            await _notify.NotifyAdminsAndLeadsAsync("ReleaseReady", $"Release '{release.Name}' ({release.Version}) is ready for sign-off.");

        return ReleaseResponse.From(release);
    }

    // The rollup + list-of-failing-cases view that makes "release status" answerable at a
    // glance — aggregates every TestRunResult across every TestRun under this release.
    [HttpGet("{id:int}/readiness-report")]
    public async Task<ActionResult> ReadinessReport(int id)
    {
        var release = await _store.GetReleaseAsync(id);
        if (release is null) return NotFound();
        if (!User.HasCompanyAccess(release.CompanyId)) return Forbid();

        var results = await _store.GetResultsForReleaseAsync(id);
        var rollup = RollupCalculator.Compute(results);
        var latest = RollupCalculator.LatestPerCase(results);
        var failingOrBlocked = latest.Where(r => r.Status is "Fail" or "Blocked")
            .Select(TestRunResultResponse.From).ToList();

        return Ok(new
        {
            release = ReleaseResponse.From(release),
            rollup,
            failingOrBlocked
        });
    }
}
