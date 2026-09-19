using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Notifications;
using FrostAura.Reel.Infrastructure.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class PushEndpoints
{
    public record SubscribeKeys(string P256dh, string Auth);

    public record SubscribeRequest(string Endpoint, SubscribeKeys Keys);

    public record UnsubscribeRequest(string Endpoint);

    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").RequireAccount();

        // Public by protocol — but still behind RequireAccount: only signed-in users have any
        // business subscribing, and the key is lazily provisioned on first request.
        group.MapGet("/vapid-public-key", async (VapidKeyProvider vapidKeys, CancellationToken ct) =>
        {
            var (publicKey, _) = await vapidKeys.GetKeysAsync(ct);
            return Results.Ok(new { key = publicKey });
        });

        group.MapPost("/subscribe", async (
            SubscribeRequest request, IAccountContext accountContext, IReelDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)
                || request.Keys is null
                || string.IsNullOrWhiteSpace(request.Keys.P256dh)
                || string.IsNullOrWhiteSpace(request.Keys.Auth))
            {
                return Results.BadRequest(new { error = "endpoint and keys are required" });
            }

            var accountId = accountContext.AccountId!.Value;
            var subscription = await db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.AccountId == accountId && s.Endpoint == request.Endpoint, ct);
            if (subscription is null)
            {
                subscription = new PushSubscription
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Endpoint = request.Endpoint,
                    CreatedAt = DateTime.UtcNow,
                };
                db.PushSubscriptions.Add(subscription);
            }

            // Re-subscribing refreshes the keys and clears failure history — the browser may
            // rotate keys on renewal while keeping the same endpoint.
            subscription.P256dh = request.Keys.P256dh;
            subscription.Auth = request.Keys.Auth;
            subscription.FailCount = 0;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // [FromBody] is explicit: minimal APIs do not infer a JSON body for DELETE.
        group.MapDelete("/subscribe", async (
            [FromBody] UnsubscribeRequest request, IAccountContext accountContext, IReelDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return Results.BadRequest(new { error = "endpoint is required" });
            }

            var accountId = accountContext.AccountId!.Value;
            await db.PushSubscriptions
                .Where(s => s.AccountId == accountId && s.Endpoint == request.Endpoint)
                .ExecuteDeleteAsync(ct);
            return Results.Ok(new { ok = true });
        });
    }
}
