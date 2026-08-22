using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Per-module link(s) to a real GitHub or Azure DevOps repo -- what an AI-generation flow needs
// to find a module's actual source code instead of guessing across dozens of repos by hand
// (the exact problem hit while building the UWMC automation project before the real Azure
// DevOps URLs were provided). Admin-only, same bar as EnvironmentsController: this exposes
// read access to proprietary source code via an encrypted, read-only PAT that is never
// returned in full once saved.
[ApiController]
[Authorize]
[Route("api/modules/{moduleId:int}/repo-links")]
public class ModuleRepoLinksController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly SecretProtector _protector;
    public ModuleRepoLinksController(IDataStore store, SecretProtector protector) { _store = store; _protector = protector; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<ModuleRepoLinkResponse>>> GetAll(int moduleId)
    {
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return NotFound("Module not found.");
        if (!User.HasCompanyAccess(module.CompanyId)) return Forbid();

        var links = await _store.GetModuleRepoLinksAsync(moduleId);
        return links.Select(ModuleRepoLinkResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ModuleRepoLinkResponse>> Create(int moduleId, CreateModuleRepoLinkRequest req)
    {
        if (!User.CanManageRepoLinks()) return Forbid();

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return NotFound("Module not found.");
        if (!User.HasCompanyAccess(module.CompanyId)) return Forbid();

        if (!Models.RepoHost.All.Contains(req.RepoHost)) return BadRequest("RepoHost must be GitHub or AzureDevOps.");
        var layer = string.IsNullOrWhiteSpace(req.Layer) ? RepoLayer.Unspecified : req.Layer;
        if (!RepoLayer.All.Contains(layer)) return BadRequest("Layer must be Frontend, Backend, Database, or Unspecified.");
        if (string.IsNullOrWhiteSpace(req.OrgOrAccount)) return BadRequest("OrgOrAccount is required.");
        if (string.IsNullOrWhiteSpace(req.RepoName)) return BadRequest("RepoName is required.");
        if (req.RepoHost == Models.RepoHost.AzureDevOps && string.IsNullOrWhiteSpace(req.Project))
            return BadRequest("Project is required for Azure DevOps repos.");

        var existing = await _store.GetModuleRepoLinksAsync(moduleId);
        if (existing.Any(l => l.Layer == layer))
            return Conflict($"This module already has a repo link for layer '{layer}' -- edit that one instead of creating a second.");

        var link = new ModuleRepoLink
        {
            ModuleId = moduleId, RepoHost = req.RepoHost, Layer = layer,
            OrgOrAccount = req.OrgOrAccount.Trim(), Project = req.Project?.Trim() ?? "",
            RepoName = req.RepoName.Trim(), Branch = string.IsNullOrWhiteSpace(req.Branch) ? "main" : req.Branch.Trim(),
            BasePath = req.BasePath?.Trim() ?? "", CreatedBy = ActorDisplayName
        };
        if (!string.IsNullOrWhiteSpace(req.AccessToken)) link.AccessTokenEncrypted = _protector.Protect(req.AccessToken);

        link = await _store.CreateModuleRepoLinkAsync(link);
        return Ok(ModuleRepoLinkResponse.From(link));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ModuleRepoLinkResponse>> Update(int moduleId, int id, CreateModuleRepoLinkRequest req)
    {
        if (!User.CanManageRepoLinks()) return Forbid();

        var link = await _store.GetModuleRepoLinkAsync(id);
        if (link is null || link.ModuleId != moduleId) return NotFound("Repo link not found.");
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null || !User.HasCompanyAccess(module.CompanyId)) return Forbid();

        if (!Models.RepoHost.All.Contains(req.RepoHost)) return BadRequest("RepoHost must be GitHub or AzureDevOps.");
        var layer = string.IsNullOrWhiteSpace(req.Layer) ? RepoLayer.Unspecified : req.Layer;
        if (!RepoLayer.All.Contains(layer)) return BadRequest("Layer must be Frontend, Backend, Database, or Unspecified.");

        link.RepoHost = req.RepoHost; link.Layer = layer;
        link.OrgOrAccount = req.OrgOrAccount.Trim(); link.Project = req.Project?.Trim() ?? "";
        link.RepoName = req.RepoName.Trim(); link.Branch = string.IsNullOrWhiteSpace(req.Branch) ? "main" : req.Branch.Trim();
        link.BasePath = req.BasePath?.Trim() ?? "";
        // Only rotate the token if a new one was actually supplied -- omitting it leaves the
        // existing encrypted token in place, same convention as EnvironmentTarget's DB strings.
        if (!string.IsNullOrWhiteSpace(req.AccessToken)) link.AccessTokenEncrypted = _protector.Protect(req.AccessToken);
        link.UpdatedBy = ActorDisplayName; link.UpdatedAt = DateTime.UtcNow;

        link = await _store.UpdateModuleRepoLinkAsync(link);
        return Ok(ModuleRepoLinkResponse.From(link));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int moduleId, int id)
    {
        if (!User.CanManageRepoLinks()) return Forbid();

        var link = await _store.GetModuleRepoLinkAsync(id);
        if (link is null || link.ModuleId != moduleId) return NotFound("Repo link not found.");
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null || !User.HasCompanyAccess(module.CompanyId)) return Forbid();

        await _store.DeleteModuleRepoLinkAsync(id);
        return NoContent();
    }
}
