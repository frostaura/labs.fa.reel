using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Saved;

namespace FrostAura.Reel.Api.Endpoints;

public static class SettingsEndpoints
{
    /// <summary>
    /// Every field is optional and null means "leave it alone" — this endpoint is PATCH-shaped
    /// despite the verb, because three unrelated screens post to it and none of them holds the
    /// others' values. <c>EveningBudgetMinutes</c> is the exception that needs stating: a
    /// non-positive value is an explicit CLEAR back to the queue's default, not a clamp to 30.
    /// </summary>
    public record UpdateSettingsRequest(string? Region, bool? Onboarded, string? Email, int? EveningBudgetMinutes);

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").RequireAccount();

        group.MapGet("/", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            return account is null
                ? Results.Unauthorized()
                : Results.Ok(Payload(account));
        });

        group.MapPut("/", async (UpdateSettingsRequest request, IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (request.Region is { Length: 2 } region)
            {
                account.Region = region.ToUpperInvariant();
            }

            if (request.Onboarded is true)
            {
                account.Settings.OnboardingCompleted = true;
            }

            if (request.Email is not null)
            {
                var email = request.Email.Trim();
                // Light validation — capture for M5 billing; null/empty clears it.
                account.Settings.EmailForBilling = email.Length == 0 ? null
                    : email is { Length: <= 254 } && email.Contains('@') && email.Contains('.') ? email
                    : account.Settings.EmailForBilling;
            }

            if (request.EveningBudgetMinutes is not null)
            {
                account.Settings.EveningBudgetMinutes = SavedQueue.NormaliseEveningBudget(request.EveningBudgetMinutes);
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(Payload(account));
        });
    }

    /// <summary>
    /// The one settings projection, so GET and PUT can never disagree about what was saved.
    /// <c>eveningBudgetMinutes</c> is served RESOLVED (never null): the Saved shelf needs a number
    /// to reason with, and "null means 150" is a rule the client should not have to know.
    /// </summary>
    private static object Payload(Domain.Tenancy.Account account) => new
    {
        region = account.Region,
        onboarded = account.Settings.OnboardingCompleted,
        email = account.Settings.EmailForBilling,
        eveningBudgetMinutes = SavedQueue.EffectiveEveningBudget(account.Settings.EveningBudgetMinutes),
    };
}
