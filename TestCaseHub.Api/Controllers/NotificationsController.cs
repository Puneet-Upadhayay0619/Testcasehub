using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly IDataStore _store;
    public NotificationsController(IDataStore store) => _store = store;

    private int CurrentUserId => int.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub) ?? "0");

    [HttpGet]
    public async Task<ActionResult<List<NotificationResponse>>> GetMine([FromQuery] bool unreadOnly = false)
    {
        var mine = await _store.GetNotificationsAsync(CurrentUserId, unreadOnly);
        return mine.Select(n => new NotificationResponse(n.Id, n.Type, n.Message, n.Read, n.CreatedAt)).ToList();
    }

    [HttpPost("{id:int}/read")]
    public async Task<ActionResult> MarkRead(int id)
    {
        var n = await _store.GetNotificationAsync(id);
        if (n is null || n.UserId != CurrentUserId) return NotFound();
        n.Read = true;
        await _store.UpdateNotificationAsync(n);
        return NoContent();
    }
}
