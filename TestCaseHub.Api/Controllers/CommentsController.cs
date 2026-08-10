using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Per-test-case discussion thread. Anyone authenticated can post (Viewer included — comments
// are about discussion, not content changes, so this is deliberately not role-gated the way
// edits are); deleting someone else's comment (moderation) is Admin/Lead only, and is a soft
// delete so there's still a record it happened.
[ApiController]
[Authorize]
[Route("api/testcases/{testCaseId}/comments")]
public class CommentsController : ControllerBase
{
    private readonly IDataStore _store;
    public CommentsController(IDataStore store) => _store = store;

    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";
    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    private async Task<bool> CanAccessTestCaseAsync(string testCaseId)
    {
        var tc = await _store.GetTestCaseAsync(testCaseId);
        if (tc is null) return false;
        var module = await _store.GetModuleAsync(tc.ModuleId);
        return module is not null && User.HasCompanyAccess(module.CompanyId);
    }

    [HttpGet]
    public async Task<ActionResult<List<CommentResponse>>> GetAll(string testCaseId)
    {
        if (!await CanAccessTestCaseAsync(testCaseId)) return Forbid();
        return (await _store.GetCommentsAsync(testCaseId)).Select(CommentResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<CommentResponse>> Add(string testCaseId, AddCommentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body)) return BadRequest("Comment body is required.");
        if (!await CanAccessTestCaseAsync(testCaseId)) return Forbid();
        var tc = await _store.GetTestCaseAsync(testCaseId);
        if (tc is null) return NotFound("Test case not found.");

        var comment = new TestCaseComment
        {
            TestCaseId = testCaseId, AuthorEmail = ActorEmail, AuthorDisplayName = ActorDisplayName, Body = req.Body.Trim()
        };
        comment = await _store.AddCommentAsync(comment);
        return CommentResponse.From(comment);
    }

    [HttpDelete("{commentId:int}")]
    public async Task<ActionResult> Delete(string testCaseId, int commentId)
    {
        var comment = await _store.GetCommentAsync(commentId);
        if (comment is null || comment.TestCaseId != testCaseId) return NotFound();
        if (!await CanAccessTestCaseAsync(testCaseId)) return Forbid();

        // Own comment: anyone can retract their own. Someone else's: Admin/Lead only (moderation).
        var isOwnComment = comment.AuthorEmail == ActorEmail;
        if (!isOwnComment && !User.IsAtLeast(Roles.Lead)) return Forbid();

        comment.Deleted = true;
        comment.DeletedBy = ActorEmail;
        await _store.UpdateCommentAsync(comment);
        return NoContent();
    }
}
