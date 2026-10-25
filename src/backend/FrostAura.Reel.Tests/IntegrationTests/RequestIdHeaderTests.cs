using FrostAura.Reel.Api.Middleware;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The real API host carries <c>X-Request-Id</c> (R1-36): <c>Harness/RequestIdTests</c> proves the
/// mechanism on a bare host; this proves <c>Program.cs</c> installs it, on an anonymous route and
/// on a refused one.
/// </summary>
[Collection("postgres")]
public class RequestIdHeaderTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task The_gateways_id_comes_back_on_health()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(RequestId.HeaderName, "9b1c2d3e4f5061728394a5b6c7d8e9f0");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("9b1c2d3e4f5061728394a5b6c7d8e9f0", Assert.Single(response.Headers.GetValues(RequestId.HeaderName)));
    }

    [Fact]
    public async Task A_refused_request_still_names_its_id()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var client = api.CreateClient();
        var response = await client.GetAsync("/api/settings");

        Assert.Equal(401, (int)response.StatusCode);
        Assert.True(RequestId.IsValid(Assert.Single(response.Headers.GetValues(RequestId.HeaderName))));
    }
}
