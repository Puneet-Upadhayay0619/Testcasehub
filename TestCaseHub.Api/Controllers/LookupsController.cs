using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/lookups")]
public class LookupsController : ControllerBase
{
    private readonly IDataStore _store;
    public LookupsController(IDataStore store) => _store = store;

    [HttpGet("priorities")]
    public async Task<ActionResult<List<string>>> GetPriorities() => await _store.GetPrioritiesAsync();

    [HttpPost("priorities")]
    public async Task<ActionResult> AddPriority(AddLookupRequest req)
    {
        if (!User.CanEditTestCases()) return Forbid();

        var val = (req.Value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(val)) return BadRequest("Value is required.");
        if (await _store.PriorityExistsAsync(val)) return Conflict("This priority already exists.");
        await _store.AddPriorityAsync(val);
        return Ok();
    }

    [HttpGet("statuses")]
    public async Task<ActionResult<List<string>>> GetStatuses() => await _store.GetStatusesAsync();

    [HttpPost("statuses")]
    public async Task<ActionResult> AddStatus(AddLookupRequest req)
    {
        if (!User.CanEditTestCases()) return Forbid();

        var val = (req.Value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(val)) return BadRequest("Value is required.");
        if (await _store.StatusExistsAsync(val)) return Conflict("This status already exists.");
        await _store.AddStatusAsync(val);
        return Ok();
    }
}
