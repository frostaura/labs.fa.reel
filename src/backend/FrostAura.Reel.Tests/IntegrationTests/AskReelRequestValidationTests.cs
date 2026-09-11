using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Every refusal <c>/api/search/ask</c> can make, asserted to be a REFUSAL — <c>{ error, code }</c>
/// with a stable code — rather than a stack trace.
///
/// <para><b>What this exists for.</b> <c>clientTurnId</c> and <c>conversationId</c> were bound as
/// <c>Guid</c> / <c>Guid?</c> on the request record, so a non-UUID, a 300-character string, a JSON
/// <c>null</c> or an empty string never reached the handler at all: JSON binding failed first and
/// ASP.NET answered with five kilobytes of <c>text/plain</c>
/// <c>BadHttpRequestException</c> — no <c>code</c>, and the internal type name
/// <c>SearchEndpoints+AskRequest</c> on a route any signed-in account can reach. The contract's own
/// warning is that a client switching on <c>code</c> renders a crash message when it gets
/// <c>undefined</c>, which is exactly what every one of those five shapes produced. The ids are
/// bound as STRINGS now and parsed by the handler, so the refusal is the handler's to make.</para>
///
/// <para>The same reasoning covers <c>mediaTypes</c>: the contract enumerates six tokens, and an
/// unknown one used to be dropped silently — a typo'd or stale filter token was indistinguishable
/// from no filter at all, and the turn was billed anyway.</para>
///
/// <para>No test here reaches the pipeline: every one is refused before a header is written, so the
/// class needs no catalogue and spends nothing.</para>
/// </summary>
[Collection("postgres")]
public class AskReelRequestValidationTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// The five shapes the finder reproduced, plus the code each one now answers with. A 300-char
    /// key is the realistic client bug — a nanoid, a <c>${tabId}-${n}</c> — and it is the one the
    /// contract used to invite by calling this field "an opaque string".
    /// </summary>
    [Theory]
    [InlineData("\"not-a-uuid\"", "client_turn_id_invalid")]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "client_turn_id_invalid")]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"", "client_turn_id_invalid")]
    [InlineData("null", "client_turn_id_required")]
    [InlineData("\"\"", "client_turn_id_required")]
    [InlineData("\"   \"", "client_turn_id_required")]
    public async Task A_malformed_client_turn_id_is_a_coded_400(string rawJson, string expectedCode)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-validate-turn-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await PostRawAsync(
                client, $$"""{"query":"a tense heist movie","clientTurnId":{{rawJson}}}""");

            await AssertCodedRefusalAsync(response, HttpStatusCode.BadRequest, expectedCode);
            await AssertNothingWasCreatedAsync(accountId);
        }
    }

    [Theory]
    [InlineData("\"nope\"")]
    [InlineData("\"12345\"")]
    [InlineData("\"\"")]
    public async Task A_malformed_conversation_id_is_a_coded_400(string rawJson)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-validate-conv-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await PostRawAsync(
                client,
                $$"""
                  {"query":"a tense heist movie","clientTurnId":"{{Guid.NewGuid()}}","conversationId":{{rawJson}}}
                  """);

            await AssertCodedRefusalAsync(response, HttpStatusCode.BadRequest, "conversation_id_invalid");
            await AssertNothingWasCreatedAsync(accountId);
        }
    }

    /// <summary>
    /// The probe: one field moved. A JSON <c>null</c> <c>conversationId</c> is the documented way to
    /// mint a fresh conversation and must stay a legitimate request — it is refused here only by the
    /// TMDB-less harness, never by validation, so the assertion is "not a validation refusal".
    /// </summary>
    [Fact]
    public async Task A_null_conversation_id_is_not_a_validation_failure()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await SignInAsync($"ask-validate-null-conv-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await PostRawAsync(
                client,
                $$"""{"query":"a tense heist movie","clientTurnId":"{{Guid.NewGuid()}}","conversationId":null}""");

            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>
    /// F5-07. <c>mediaTypes: ["cartoon"]</c> returned a full, billed 24-pick turn with no warning
    /// anywhere on the wire. A filter token the server cannot honour is a request the server cannot
    /// answer, and the user is owed that before the money is spent, not after.
    /// </summary>
    [Theory]
    [InlineData("[\"cartoon\"]")]
    [InlineData("[\"movie\",\"cartoon\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[null]")]
    public async Task An_unknown_media_type_token_is_a_coded_400(string rawJson)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync($"ask-validate-media-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await PostRawAsync(
                client,
                $$"""
                  {"query":"a tense heist movie","clientTurnId":"{{Guid.NewGuid()}}","mediaTypes":{{rawJson}}}
                  """);

            await AssertCodedRefusalAsync(response, HttpStatusCode.BadRequest, "media_type_invalid");
            await AssertNothingWasCreatedAsync(accountId);
        }
    }

    /// <summary>All six documented tokens survive; the enumeration in the contract is the whole set.</summary>
    [Theory]
    [InlineData("movie")]
    [InlineData("movies")]
    [InlineData("film")]
    [InlineData("tv")]
    [InlineData("show")]
    [InlineData("series")]
    public async Task Every_documented_media_type_token_is_accepted(string token)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await SignInAsync($"ask-validate-media-ok-{Guid.NewGuid():N}");
        using (client)
        {
            using var response = await PostRawAsync(
                client,
                $$"""
                  {"query":"a tense heist movie","clientTurnId":"{{Guid.NewGuid()}}","mediaTypes":["{{token}}"]}
                  """);

            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>
    /// The house shape, and nothing of ours in it. The internal type name leaking
    /// (<c>FrostAura.Reel.Api.Endpoints.SearchEndpoints+AskRequest</c>) was half of F5-01's
    /// severity: it is a reachable route for any signed-in account.
    /// </summary>
    private static async Task AssertCodedRefusalAsync(
        HttpResponseMessage response, HttpStatusCode status, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("error").GetString()));
        Assert.DoesNotContain("FrostAura.Reel", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    /// <summary>A pre-spend refusal writes nothing — no conversation, no turn, no card.</summary>
    private async Task AssertNothingWasCreatedAsync(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        await using var db = fixture.CreateContext(scope);
        Assert.Equal(0, await db.SearchConversations.CountAsync());
        Assert.Equal(0, await db.SearchTurns.CountAsync());
    }

    /// <summary>
    /// Raw JSON rather than <see cref="HttpClientJsonExtensions.PostAsJsonAsync{TValue}(HttpClient, string, TValue, CancellationToken)"/>:
    /// the whole point is to send shapes a typed object cannot express.
    /// </summary>
    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string json) =>
        client.PostAsync("/api/search/ask", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        return (accountId, await api.CreateSignedInClientAsync(accountId));
    }
}
