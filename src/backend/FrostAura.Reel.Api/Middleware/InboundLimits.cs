using System.Globalization;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Middleware;

/// <summary>
/// Inbound request limits that sit in front of a route rather than inside it (J-8, B-14):
/// <list type="bullet">
/// <item><c>POST /api/search/ask</c> — the per-account turn limits (<see cref="AskTurnLimiter"/>).
/// The in-flight slot is held until the whole streamed response has been written.</item>
/// <item><c>/api/dev/*</c> — at most <see cref="DevConcurrency"/> requests at once, process-wide.
/// Several of those routes start or read whole-catalogue work; two terminals hammering them should
/// queue on the founder, not on the database.</item>
/// </list>
/// Both refusals are 429 with <c>Retry-After</c> and the house error shape plus a stable
/// <c>code</c>. Runs after <see cref="AccountResolutionMiddleware"/> (it partitions by account) and
/// before routing's endpoint executes; an anonymous ask passes through to the route's own 401.
///
/// <para>The PIN limiter is not here: a wrong PIN is only known inside the handler, so
/// <see cref="FilterEndpoints"/> applies <see cref="PinAttemptLimiter"/> at each PIN check.</para>
/// </summary>
public sealed class InboundLimits(RequestDelegate next, AskTurnLimiter askTurns)
{
    public const string AskPath = "/api/search/ask";

    /// <summary>Concurrent <c>/api/dev/*</c> requests allowed across the process.</summary>
    public const int DevConcurrency = 2;

    /// <summary>Per pipeline, not static: one host per process in production, several in the test run.</summary>
    private readonly SemaphoreSlim devSlots = new(DevConcurrency, DevConcurrency);

    public async Task InvokeAsync(HttpContext context, IAccountContext accountContext, IReelDbContext db)
    {
        var path = context.Request.Path;
        if (HttpMethods.IsPost(context.Request.Method)
            && path.Equals(AskPath, StringComparison.OrdinalIgnoreCase)
            && accountContext.AccountId is { } accountId)
        {
            var tier = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == accountId)
                .Select(a => (Domain.Tenancy.AccountTier?)a.Tier)
                .FirstOrDefaultAsync(context.RequestAborted);
            if (tier is null)
            {
                await next(context); // a token for a deleted account: the route answers 401
                return;
            }

            var (lease, refusal) = askTurns.TryStart(accountId, tier.Value);
            if (refusal is not null)
            {
                await RefuseAsync(context, refusal.Code, refusal.Message, refusal.RetryAfter);
                return;
            }

            using (lease)
            {
                await next(context);
            }

            return;
        }

        if (path.StartsWithSegments(DevEndpoints.DevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!await devSlots.WaitAsync(TimeSpan.Zero, context.RequestAborted))
            {
                await RefuseAsync(
                    context,
                    "dev_busy",
                    $"{DevConcurrency.ToString(CultureInfo.InvariantCulture)} diagnostic requests are already running; retry when one finishes.",
                    TimeSpan.FromSeconds(5));
                return;
            }

            try
            {
                await next(context);
            }
            finally
            {
                devSlots.Release();
            }

            return;
        }

        await next(context);
    }

    private static Task RefuseAsync(HttpContext context, string code, string message, TimeSpan retryAfter)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return context.Response.WriteAsJsonAsync(new { error = message, code, retryAfterSeconds = seconds }, context.RequestAborted);
    }
}
