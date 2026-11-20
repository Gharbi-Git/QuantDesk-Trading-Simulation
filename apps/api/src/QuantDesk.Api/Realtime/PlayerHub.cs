using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace QuantDesk.Api.Realtime;

// Only authenticated PLAYER users can connect.
[Authorize(Policy = "PlayerOnly")]
public sealed class PlayerHub : Hub
{
    public override Task OnConnectedAsync()
    {
        // Every connection must belong to a real QuantDesk user.
        var userId = Context.User?
            .FindFirst("quantdesk_user_id")?.Value;

        if (!Guid.TryParse(userId, out _))
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        return base.OnConnectedAsync();
    }

    // Simple server round-trip to verify the connection.
    public string Ping() => "pong";
}

// Map SignalR's user identity to our INTERNAL QuantDesk user ID,
// not a username supplied by the browser.
public sealed class QuantDeskUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User?
            .FindFirst("quantdesk_user_id")?.Value;
    }
}

// Other backend services will use this to send private notifications.
// No public endpoint allows clients to choose another player's ID.
public sealed class PlayerNotificationPublisher(
    IHubContext<PlayerHub> hub)
{
    public Task NotifyAsync(
        Guid playerId,
        string message,
        CancellationToken cancellationToken = default)
    {
        return hub.Clients
            .User(playerId.ToString("D"))
            .SendAsync(
                "PlayerNotification",
                message,
                cancellationToken);
    }
}
