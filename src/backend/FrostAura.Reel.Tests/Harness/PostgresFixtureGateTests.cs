using FrostAura.Reel.Tests.IntegrationTests;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// The integration suite's run/skip/fail decision. Offline — this is the pure half of
/// <see cref="PostgresFixture"/>, deliberately extracted so the behaviour that keeps CI honest
/// is itself under test rather than only observable by breaking CI.
/// </summary>
public class PostgresFixtureGateTests
{
    [Fact]
    public void A_connection_string_always_runs_the_suite()
    {
        Assert.Equal(
            IntegrationGateDecision.Run,
            PostgresFixtureGate.Decide("Host=localhost;Database=x", requireValue: null));

        // Required or not makes no difference once a database is on offer.
        Assert.Equal(
            IntegrationGateDecision.Run,
            PostgresFixtureGate.Decide("Host=localhost;Database=x", requireValue: "1"));
    }

    [Fact]
    public void No_connection_string_and_no_demand_skips_silently()
    {
        // The local default: `dotnet test` with no database must stay green.
        Assert.Equal(IntegrationGateDecision.SkipSilently, PostgresFixtureGate.Decide(null, null));
        Assert.Equal(IntegrationGateDecision.SkipSilently, PostgresFixtureGate.Decide(null, string.Empty));
        Assert.Equal(IntegrationGateDecision.SkipSilently, PostgresFixtureGate.Decide(null, "0"));
        Assert.Equal(IntegrationGateDecision.SkipSilently, PostgresFixtureGate.Decide(null, "true"));
    }

    [Fact]
    public void No_connection_string_under_the_demand_fails_the_collection()
    {
        // CI's contract: 21 integration files reporting green having run nothing is a build
        // failure, not a pass.
        Assert.Equal(IntegrationGateDecision.Fail, PostgresFixtureGate.Decide(null, "1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_counts_as_absent(string connectionString)
    {
        // An empty environment variable is how a mis-wired CI job presents itself; it must not
        // read as "available" and then fail obscurely inside Npgsql.
        Assert.Equal(IntegrationGateDecision.Fail, PostgresFixtureGate.Decide(connectionString, "1"));
        Assert.Equal(IntegrationGateDecision.SkipSilently, PostgresFixtureGate.Decide(connectionString, null));
    }

    [Fact]
    public void Only_the_exact_flag_value_demands_a_database()
    {
        Assert.True(PostgresFixtureGate.IsRequired("1"));
        Assert.False(PostgresFixtureGate.IsRequired(null));
        Assert.False(PostgresFixtureGate.IsRequired(string.Empty));
        Assert.False(PostgresFixtureGate.IsRequired("yes"));
    }

    [Fact]
    public void The_failure_messages_name_both_variables_so_the_fix_is_obvious()
    {
        Assert.Contains(PostgresFixtureGate.ConnectionStringVariable, PostgresFixtureGate.MissingConnectionStringMessage);
        Assert.Contains(PostgresFixtureGate.RequireVariable, PostgresFixtureGate.MissingConnectionStringMessage);
        Assert.Contains("boom", PostgresFixtureGate.UnreachableMessage(new InvalidOperationException("boom")));
    }
}
