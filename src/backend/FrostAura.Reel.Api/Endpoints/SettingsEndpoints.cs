using System.Net.Mail;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Api.Endpoints;

public static class SettingsEndpoints
{
    /// <summary>
    /// Every field is optional and null means "leave it alone" — this endpoint is PATCH-shaped
    /// despite the verb, because three unrelated screens post to it and none of them holds the
    /// others' values. <c>EveningBudgetMinutes</c> is the exception that needs stating: a
    /// non-positive value is an explicit CLEAR back to the queue's default, not a clamp to 30.
    /// <c>TimeZone</c> is an IANA id; an empty string clears it back to UTC.
    /// </summary>
    public record UpdateSettingsRequest(string? Region, bool? Onboarded, string? Email, int? EveningBudgetMinutes, string? TimeZone = null);

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

            // Validate everything before writing anything: a PUT carrying one bad field changes
            // nothing, and says which field (H-4, H-11). Previously a malformed email was silently
            // dropped (the old address kept, 200 returned) and any two letters became the region.
            string? region = null;
            if (request.Region is not null)
            {
                region = RegionCodes.Normalise(request.Region);
                if (region is null)
                {
                    return Results.BadRequest(new { error = "region must be an ISO 3166-1 alpha-2 country code", code = "region_invalid" });
                }
            }

            string? email = null;
            var clearEmail = false;
            if (request.Email is not null)
            {
                email = request.Email.Trim();
                clearEmail = email.Length == 0;
                if (!clearEmail && !IsPlausibleEmail(email))
                {
                    return Results.BadRequest(new { error = "email is not a valid address", code = "email_invalid" });
                }
            }

            string? timeZone = null;
            var clearTimeZone = false;
            if (request.TimeZone is not null)
            {
                clearTimeZone = request.TimeZone.Trim().Length == 0;
                timeZone = clearTimeZone ? null : AccountTimeZone.Normalise(request.TimeZone);
                if (!clearTimeZone && timeZone is null)
                {
                    return Results.BadRequest(new { error = "timeZone must be an IANA time zone such as Africa/Johannesburg", code = "time_zone_invalid" });
                }
            }

            if (region is not null)
            {
                account.Region = region;
            }

            if (request.TimeZone is not null)
            {
                account.Settings.TimeZone = clearTimeZone ? null : timeZone;
            }

            if (request.Onboarded is true)
            {
                account.Settings.OnboardingCompleted = true;
            }

            if (request.Email is not null)
            {
                // Captured for M5 billing; an empty string clears it.
                account.Settings.EmailForBilling = clearEmail ? null : email;
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
    /// A single address a mail server would accept the shape of: what <see cref="MailAddress"/>
    /// parses back to itself (no display name, no comments, no list), at most 254 characters, with a
    /// dotted domain. Deliverability is billing's problem; shape is this endpoint's.
    /// </summary>
    internal static bool IsPlausibleEmail(string email) =>
        email.Length <= 254
        && MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal)
        && parsed.Host.Contains('.', StringComparison.Ordinal)
        && !parsed.Host.StartsWith('.')
        && !parsed.Host.EndsWith('.');

    /// <summary>
    /// The one settings projection, so GET and PUT can never disagree about what was saved.
    /// <c>eveningBudgetMinutes</c> is served RESOLVED (never null): the Saved shelf needs a number
    /// to reason with, and "null means 150" is a rule the client should not have to know.
    /// <c>timeZone</c> is served AS STORED (null when never set) beside the zone actually in force,
    /// so the client can offer the browser's zone to an account that has not chosen one instead of
    /// presenting UTC as a choice the user made.
    /// </summary>
    private static object Payload(Domain.Tenancy.Account account) => new
    {
        region = account.Region,
        onboarded = account.Settings.OnboardingCompleted,
        email = account.Settings.EmailForBilling,
        eveningBudgetMinutes = SavedQueue.EffectiveEveningBudget(account.Settings.EveningBudgetMinutes),
        timeZone = account.Settings.TimeZone,
        effectiveTimeZone = AccountTimeZone.Resolve(account.Settings.TimeZone).Id,
    };
}
