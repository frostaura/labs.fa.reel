using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class EndpointHelpers
{
    /// <summary>401 guard for protected endpoint groups — the account scope must be pinned.</summary>
    public static RouteGroupBuilder RequireAccount(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var accountContext = context.HttpContext.RequestServices.GetRequiredService<IAccountContext>();
            return accountContext.AccountId is null
                ? Results.Unauthorized()
                : await next(context);
        });
        return group;
    }

    /// <summary>
    /// 403 guard for endpoint groups that only some tiers may reach. Composes AFTER
    /// <see cref="RequireAccount"/> — it answers 401 for an unpinned scope rather than 403, because
    /// "you are not signed in" and "your tier is not enough" are different facts and a client that
    /// cannot tell them apart will retry the wrong one forever.
    ///
    /// <para>It resolves the account the same way <see cref="GetCurrentAccountAsync"/> does rather
    /// than trusting a claim, because tier lives on the row and is changed by billing without the
    /// session cookie being reissued.</para>
    ///
    /// <para><b>Hazard, inherited deliberately.</b> <c>Results.Forbid()</c> is the house shape for
    /// 403 here (eight existing sites use it), but this app registers no ASP.NET Core
    /// authentication handler, so <c>ForbidAsync</c> has no scheme to dispatch to. Matching the
    /// house is the right call for a contracts-only change; whoever first WIRES this filter onto a
    /// route should verify the status code against the running app rather than assume 403.</para>
    /// </summary>
    public static RouteGroupBuilder RequireTier(this RouteGroupBuilder group, params AccountTier[] tiers)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<IReelDbContext>();
            var account = await context.HttpContext.GetCurrentAccountAsync(db, context.HttpContext.RequestAborted);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            return tiers.Contains(account.Tier)
                ? await next(context)
                : Results.Forbid();
        });
        return group;
    }

    /// <summary>
    /// The founder-only case of <see cref="RequireTier"/>, named rather than spelled out at every
    /// call site — the diagnostic surfaces are gated on exactly this and a typo'd tier list on one
    /// of them would open a founder surface to every paying account.
    /// </summary>
    public static RouteGroupBuilder RequireFounder(this RouteGroupBuilder group) =>
        group.RequireTier(AccountTier.Founder);

    public static async Task<Account?> GetCurrentAccountAsync(this HttpContext httpContext, IReelDbContext db, CancellationToken ct)
    {
        var accountContext = httpContext.RequestServices.GetRequiredService<IAccountContext>();
        if (accountContext.AccountId is null)
        {
            return null;
        }

        return await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountContext.AccountId.Value, ct);
    }

    public static object ToSessionPayload(this Account account) => new
    {
        accountId = account.Id,
        displayName = account.DisplayName,
        traktSlug = account.TraktUserSlug,
        avatarUrl = account.AvatarUrl,
        region = account.Region,
        tier = account.Tier.ToString(),
        pipelineStage = account.PipelineStage.ToString(),
        onboarded = account.Settings.OnboardingCompleted,
        pinConfigured = account.Settings.SettingsPinHash is not null,
    };
}
