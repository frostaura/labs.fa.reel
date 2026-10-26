using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class EndpointHelpers
{
    /// <summary>
    /// The house 403. Every tier gate answers through this rather than through
    /// <c>Results.Forbid()</c>, and the difference is not stylistic.
    ///
    /// <para><b>Why <c>Results.Forbid()</c> cannot be used here.</b> It resolves to
    /// <c>HttpContext.ForbidAsync()</c>, which asks the container for <c>IAuthenticationService</c>
    /// and dispatches to the default forbid scheme. This app registers no ASP.NET Core
    /// authentication handler at all — identity is the <c>reel_at</c> cookie, resolved by
    /// <c>AccountResolutionMiddleware</c> — so that lookup throws
    /// <c>InvalidOperationException: Unable to find the required 'IAuthenticationService' service</c>
    /// and the request ends 500. Every founder-only route in this API did exactly that, and nothing
    /// noticed for the obvious reason: the only account that exercises those routes is Founder
    /// tier, so the deny branch was unreachable in practice. Pinned by
    /// <c>IntegrationTests/TierGateTests.cs</c> and by the source guard in
    /// <c>Tests/Harness/ForbidResultGuardTests.cs</c>.</para>
    ///
    /// <para>The shape is the house error envelope — <c>{ "error": "…" }</c>, as
    /// <c>BillingEndpoints</c> returns for its 409 — written with the same
    /// <c>Results.Json(…, statusCode:)</c> form, so a client parses one body shape for every
    /// refusal.</para>
    ///
    /// <para>Adding an authentication scheme purely to make <c>Forbid()</c> work is deliberately
    /// NOT the fix: it would introduce a second, competing identity stack alongside the cookie
    /// middleware that is actually authoritative here.</para>
    /// </summary>
    internal static IResult Forbidden(string code = "forbidden") =>
        Results.Json(new { error = code }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>401 guard for protected endpoints and groups — the account scope must be pinned.</summary>
    public static TBuilder RequireAccount<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var accountContext = context.HttpContext.RequestServices.GetRequiredService<IAccountContext>();
            return accountContext.AccountId is null
                ? Results.Unauthorized()
                : await next(context);
        });
        return builder;
    }

    /// <summary>
    /// 403 guard for endpoints or groups that only some tiers may reach. Composes AFTER
    /// <see cref="RequireAccount"/> — it answers 401 for an unpinned scope rather than 403, because
    /// "you are not signed in" and "your tier is not enough" are different facts and a client that
    /// cannot tell them apart will retry the wrong one forever.
    ///
    /// <para>It resolves the account the same way <see cref="GetCurrentAccountAsync"/> does rather
    /// than trusting a claim, because tier lives on the row and is changed by billing without the
    /// session cookie being reissued. The row it loaded is kept on the request, so a handler behind
    /// the gate reads it with <see cref="GatedAccount"/> instead of loading and re-checking it.</para>
    ///
    /// <para>The deny answer comes from <see cref="Forbidden"/>, not <c>Results.Forbid()</c> —
    /// which threw here rather than answering 403. The reasoning is on that helper.</para>
    /// </summary>
    public static TBuilder RequireTier<TBuilder>(this TBuilder builder, params AccountTier[] tiers)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<IReelDbContext>();
            var account = await context.HttpContext.GetCurrentAccountAsync(db, context.HttpContext.RequestAborted);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (!tiers.Contains(account.Tier))
            {
                return Forbidden("tier not permitted");
            }

            context.HttpContext.Items[GatedAccountKey] = account;
            return await next(context);
        });
        return builder;
    }

    /// <summary>
    /// The founder-only case of <see cref="RequireTier"/>, named rather than spelled out at every
    /// call site — the diagnostic surfaces are gated on exactly this and a typo'd tier list on one
    /// of them would open a founder surface to every paying account. Applies to a single route as
    /// well as a group: a founder-only route inside an every-account group takes it on the route,
    /// never as an inline tier comparison in its handler (J-11 — seven such comparisons existed,
    /// each one a place the rule could be mistyped, and none visible to the authorisation matrix).
    /// </summary>
    public static TBuilder RequireFounder<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireTier(AccountTier.Founder);

    /// <summary>The <c>HttpContext.Items</c> key under which <see cref="RequireTier"/> keeps the account it admitted.</summary>
    private static readonly object GatedAccountKey = new();

    /// <summary>
    /// The account a <see cref="RequireTier"/> / <see cref="RequireFounder"/> gate admitted on this
    /// request. Throws when no gate ran: a handler that calls this without the gate is a wiring bug,
    /// and failing loudly is what keeps it from becoming an open route.
    /// </summary>
    public static Account GatedAccount(this HttpContext httpContext) =>
        httpContext.Items[GatedAccountKey] as Account
        ?? throw new InvalidOperationException("GatedAccount() was called on a route with no RequireTier/RequireFounder gate.");

    /// <summary>
    /// Whether a request may see founder-only diagnostics on an endpoint that is NOT itself
    /// founder-only — <c>?debug=1</c> from a Founder-tier account, and nothing else.
    ///
    /// <para>Separate from <see cref="RequireFounder"/> on purpose: that filter gates a whole route,
    /// and these routes are open to every paying account. Only one FIELD of the answer is
    /// privileged, so the decision has to be taken inside the handler. Kept here, as a pure
    /// function of the two inputs, so the rule is one line in one place and can be pinned without a
    /// host — the alternative was an inline comparison on every such handler, which is how a
    /// diagnostic surface ends up open by a typo.</para>
    ///
    /// <para>The tier must be read from the account ROW rather than a claim: billing changes it
    /// without reissuing the session cookie.</para>
    /// </summary>
    public static bool AllowsFounderDebug(string? debugFlag, AccountTier? tier) =>
        debugFlag == "1" && tier == AccountTier.Founder;

    public static async Task<Account?> GetCurrentAccountAsync(this HttpContext httpContext, IReelDbContext db, CancellationToken ct)
    {
        var accountContext = httpContext.RequestServices.GetRequiredService<IAccountContext>();
        if (accountContext.AccountId is null)
        {
            return null;
        }

        return await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountContext.AccountId.Value, ct);
    }

    /// <summary>
    /// The session payload auth/callback, auth/me and dev/link return. <c>pipelineStage</c> is the
    /// RESOLVED stage — the one <c>/api/sync/status</c> reports — not the stored column: a stored
    /// Degraded on a healthy link below the ratings floor reads NotEnoughRatings in both, so the
    /// shell and the status pill can never disagree about where the account is (WS-6 CR-4).
    /// </summary>
    public static async Task<object> ToSessionPayloadAsync(this Account account, HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<IReelDbContext>();
        var connectionStatus = await db.TraktConnections.AsNoTracking()
            .Where(c => c.AccountId == account.Id)
            .Select(c => (Domain.Sync.ConnectionStatus?)c.Status)
            .FirstOrDefaultAsync(ct) ?? Domain.Sync.ConnectionStatus.Revoked;
        var (stage, _) = await http.RequestServices.GetRequiredService<SyncStatusQuery>()
            .ResolveStageAsync(account.Id, account.PipelineStage, connectionStatus, ct);
        return new
        {
            accountId = account.Id,
            displayName = account.DisplayName,
            traktSlug = account.TraktUserSlug,
            avatarUrl = account.AvatarUrl,
            region = account.Region,
            tier = account.Tier.ToString(),
            pipelineStage = stage.ToString(),
            onboarded = account.Settings.OnboardingCompleted,
            pinConfigured = account.Settings.SettingsPinHash is not null,
        };
    }
}
