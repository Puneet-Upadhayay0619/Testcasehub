using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/modules")]
public class ModulesController : ControllerBase
{
    private readonly IDataStore _store;
    public ModulesController(IDataStore store) => _store = store;

    [HttpGet]
    public async Task<ActionResult<List<ModuleResponse>>> GetAll()
    {
        var modules = await _store.GetModulesAsync();
        var counts = await _store.GetTestCaseCountsByModuleAsync();
        return modules.Select(m => new ModuleResponse(
            m.Id, m.Name, m.Code, m.Description, m.Owner, m.Status, m.CreatedAt,
            counts.TryGetValue(m.Id, out var c) ? c : 0
        )).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(ModuleCreateRequest req)
    {
        // Agreed in planning: module creation is Contributor and above (Viewer cannot).
        if (!User.CanCreateModule())
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest("Module name and code are required.");

        var code = req.Code.Trim().ToUpperInvariant();
        if (await _store.ModuleCodeExistsAsync(code))
            return Conflict($"A module with code '{code}' already exists.");

        var module = new Module
        {
            Name = req.Name.Trim(), Code = code, Description = req.Description ?? "",
            Owner = req.Owner ?? "", Status = string.IsNullOrWhiteSpace(req.Status) ? "Active" : req.Status
        };
        module = await _store.CreateModuleAsync(module);
        return Ok(new ModuleResponse(module.Id, module.Name, module.Code, module.Description, module.Owner, module.Status, module.CreatedAt, 0));
    }

    [HttpPost("{moduleId:int}/task-links")]
    public async Task<ActionResult<TaskLinkResponse>> AddTaskLink(int moduleId, TaskLinkCreateRequest req)
    {
        if (!User.CanEditTaskLinks()) return Forbid();

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return NotFound("Module not found.");
        if (string.IsNullOrWhiteSpace(req.AdoTaskId)) return BadRequest("Task ID is required.");

        var link = new TaskLink
        {
            ModuleId = moduleId, Layer = req.Layer, AdoProject = req.AdoProject,
            AdoTaskId = req.AdoTaskId.Trim(), AdoTaskTitle = req.AdoTaskTitle ?? "", AdoTaskUrl = req.AdoTaskUrl ?? ""
        };
        link = await _store.CreateTaskLinkAsync(link);
        return Ok(new TaskLinkResponse(link.Id, link.ModuleId, link.Layer, link.AdoProject, link.AdoTaskId, link.AdoTaskTitle, link.AdoTaskUrl, link.LinkedAt));
    }

    [HttpGet("{moduleId:int}/task-links")]
    public async Task<ActionResult<List<TaskLinkResponse>>> GetTaskLinks(int moduleId)
    {
        var links = await _store.GetTaskLinksAsync(moduleId);
        return links.Select(l => new TaskLinkResponse(l.Id, l.ModuleId, l.Layer, l.AdoProject, l.AdoTaskId, l.AdoTaskTitle, l.AdoTaskUrl, l.LinkedAt)).ToList();
    }
}
