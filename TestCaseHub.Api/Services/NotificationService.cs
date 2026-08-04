using TestCaseHub.Api.Models;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Services;

// Centralizes the three notification triggers agreed in planning (automation failure, role
// change, release ready-for-signoff) so every call site creates the same shape of in-app
// notification instead of duplicating logic. In-app only for now — a real delivery channel
// (email/Teams/Slack) was left as a later decision.
public class NotificationService
{
    private readonly IDataStore _store;
    public NotificationService(IDataStore store) => _store = store;

    public Task NotifyUserAsync(int userId, string type, string message) =>
        _store.AddNotificationAsync(new Notification { UserId = userId, Type = type, Message = message });

    // Fan-out to every active Admin/Lead — used for things the whole "release owns this"
    // group should see (automation failures, a release reaching sign-off).
    public async Task NotifyAdminsAndLeadsAsync(string type, string message)
    {
        var admins = await _store.GetUsersByRoleAsync(Roles.Admin);
        var leads = await _store.GetUsersByRoleAsync(Roles.Lead);
        foreach (var u in admins.Concat(leads))
            await _store.AddNotificationAsync(new Notification { UserId = u.Id, Type = type, Message = message });
    }
}
