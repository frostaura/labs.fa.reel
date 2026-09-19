using System.Net;
using System.Net.Http.Json;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What a founder-only route actually answers a signed-in account that is not the founder.
///
/// <para><b>Why this class exists.</b> Every tier gate in this API returned
/// <c>Results.Forbid()</c>, which calls <c>HttpContext.ForbidAsync()</c> and dispatches to the
/// default forbid scheme of the ASP.NET Core authentication stack. This app registers no
/// authentication handler at all — identity is the <c>reel_at</c> cookie resolved by
/// <c>AccountResolutionMiddleware</c> — so there IS no default scheme, and every one of those
/// sites threw <c>InvalidOperationException: No authenticationScheme was specified, and there was
/// no DefaultForbidScheme found</c> instead of answering 403. A client saw a 500, or an aborted
/// response, and could not tell "your tier is not enough" from "the server fell over".</para>
///
/// <para>The reason it survived so long is that nothing exercised it: the founder's own account is
/// Founder tier, so the deny branch never ran outside a test that did not exist. That is the shape
/// of the whole defect class — a guard whose failure path is only reachable by the users who are
/// least able to report it.</para>
///
/// <para>The fix is <c>EndpointHelpers.Forbidden</c>: an explicit JSON 403 in the house error
/// shape, taking no dependency on an authentication stack this app does not have. These tests pin
/// the answered status codes, not the helper, because the helper is an implementation detail and
/// the status code is the contract.</para>
///
/// <para><b>The namespace is load-bearing</b> — CI filters the integration job on
/// <c>FullyQualifiedName~IntegrationTests</c>.</para>
/// </summary>
[Collection("postgres")]
public class TierGateTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// A founder-only route that needs nothing seeded to answer: it computes its reading from the
    /// exposure log on read, and an empty log is a legitimate (all-zero) reading rather than an
    /// error. Chosen for that reason — a gate test that also needs a pipeline is a gate test that
    /// goes red for pipeline reasons.
    /// </summary>
    private const string FounderOnlyRoute = "/api/metrics/interleave";

    [Fact]
    public async Task Anonymous_request_to_a_founder_only_route_is_401()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var client = api.CreateClient();
        using var response = await client.GetAsync(FounderOnlyRoute);

        // 401, not 403: "you are not signed in" and "your tier is not enough" are different facts,
        // and the group's RequireAccount filter answers first.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_paid_account_on_a_founder_only_route_is_403_in_the_house_error_shape()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"tier-gate-paid-{Guid.NewGuid():N}");
        using (client)
        {
            await SetTierAsync(accountId, AccountTier.Paid);

            using var response = await client.GetAsync(FounderOnlyRoute);

            // This is the assertion that was 500 before the fix.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
            Assert.NotNull(body);
            Assert.False(string.IsNullOrWhiteSpace(body!.Error));
        }
    }

    [Fact]
    public async Task A_founder_account_reaches_the_founder_only_route()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"tier-gate-founder-{Guid.NewGuid():N}");
        using (client)
        {
            await SetTierAsync(accountId, AccountTier.Founder);

            using var response = await client.GetAsync(FounderOnlyRoute);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// The fixture probe: ONE account, ONE route, one field varied. A gate asserted with three
    /// separately-seeded accounts can pass because of something else those accounts differ in;
    /// flipping the single field that is supposed to decide the answer is what makes the tier the
    /// cause rather than a correlate.
    /// </summary>
    [Fact]
    public async Task The_tier_field_alone_decides_the_answer()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"tier-gate-probe-{Guid.NewGuid():N}");
        using (client)
        {
            var observed = new List<(AccountTier Tier, HttpStatusCode Status)>();
            foreach (var tier in new[] { AccountTier.Free, AccountTier.Paid, AccountTier.Founder })
            {
                await SetTierAsync(accountId, tier);
                using var response = await client.GetAsync(FounderOnlyRoute);
                observed.Add((tier, response.StatusCode));
            }

            Assert.Equal(
                [
                    (AccountTier.Free, HttpStatusCode.Forbidden),
                    (AccountTier.Paid, HttpStatusCode.Forbidden),
                    (AccountTier.Founder, HttpStatusCode.OK),
                ],
                observed);
        }
    }

    /// <summary>
    /// Writes the tier through the host's own context, with query filters ignored — the tier is
    /// read from the account ROW by every gate, precisely because billing changes it without the
    /// session cookie being reissued, so the same cookie must observe the new answer.
    /// </summary>
    private async Task SetTierAsync(Guid accountId, AccountTier tier)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        account.Tier = tier;
        await db.SaveChangesAsync();
    }

    private sealed record ErrorBody(string Error);
}
