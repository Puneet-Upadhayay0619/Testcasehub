using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Admin-only management of CI/pipeline service-account keys (Phase 6). Deliberately separate
// from human login — see Models/Automation.cs for the reasoning.
[ApiController]
[Authorize]
[Route("api/apikeys")]
public class ApiKeysController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly ApiKeyService _keys;
    public ApiKeysController(IDataStore store, ApiKeyService keys) { _store = store; _keys = keys; }

    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";
    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<ApiKeyResponse>>> GetAll()
    {
        if (!User.CanManageUsers()) return Forbid(); // same "Admin only" gate as user management
        return (await _store.GetApiKeysAsync()).Select(ApiKeyResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<IssuedApiKeyResponse>> Create(CreateApiKeyRequest req)
    {
        if (!User.CanManageUsers()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("A name is required (e.g. 'Azure DevOps CI').");

        var (raw, key) = await _keys.IssueAsync(req.Name.Trim(), req.Scope ?? "ReportResults", ActorDisplayName);
        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "ApiKeyCreated",
            TargetDescription = key.Name, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { key.Id, key.Scope })
        });
        // The raw key is returned exactly once here — it cannot be recovered after this response.
        return new IssuedApiKeyResponse(key.Id, key.Name, raw);
    }

    [HttpPost("{id:int}/revoke")]
    public async Task<ActionResult<ApiKeyResponse>> Revoke(int id)
    {
        if (!User.CanManageUsers()) return Forbid();
        var key = await _store.GetApiKeyAsync(id);
        if (key is null) return NotFound();
        key.Revoked = true;
        key = await _store.UpdateApiKeyAsync(key);

        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "ApiKeyRevoked",
            TargetDescription = key.Name, DetailsJson = "{}"
        });
        return ApiKeyResponse.From(key);
    }
}
