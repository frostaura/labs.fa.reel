namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>What <see cref="PostgresFixture"/> should do about its database, before it tries.</summary>
public enum IntegrationGateDecision
{
    /// <summary>A connection string is present — migrate and run the suite.</summary>
    Run,

    /// <summary>No connection string and none demanded — no-op so a plain local run stays green.</summary>
    SkipSilently,

    /// <summary>No connection string, but the environment demanded one — fail the whole collection.</summary>
    Fail,
}

/// <summary>
/// The integration suite's run/skip/fail decision, extracted as a pure function so it can be
/// asserted without an environment or a database.
///
/// The problem it closes: with no connection string every integration test early-returned on
/// <c>fixture.Available</c>, so 21 files reported GREEN having executed nothing — including the
/// real-Postgres tenancy audit. A silent pass is the worst possible outcome, because it looks
/// exactly like a real one. Locally that silence is still wanted (nobody should need Docker to
/// run the unit suite); in CI it is a lie, so CI sets <see cref="RequireVariable"/> and the
/// fixture throws instead.
/// </summary>
public static class PostgresFixtureGate
{
    /// <summary>Connection string for a THROWAWAY database — the fixture drops and re-migrates it.</summary>
    public const string ConnectionStringVariable = "REEL_TEST_CONNECTION_STRING";

    /// <summary>Set to <c>1</c> where a skipped integration suite must be a build failure (CI).</summary>
    public const string RequireVariable = "REEL_REQUIRE_INTEGRATION";

    /// <summary>Only the exact string "1" demands a database; anything else leaves the local silence intact.</summary>
    public static bool IsRequired(string? requireValue) => requireValue == "1";

    /// <summary>
    /// A blank connection string counts as absent: an empty environment variable is how a
    /// mis-wired CI job presents itself, and treating it as "available" would fail later, deep
    /// inside Npgsql, instead of here where the message can say what is actually wrong.
    /// </summary>
    public static IntegrationGateDecision Decide(string? connectionString, string? requireValue) =>
        !string.IsNullOrWhiteSpace(connectionString)
            ? IntegrationGateDecision.Run
            : IsRequired(requireValue)
                ? IntegrationGateDecision.Fail
                : IntegrationGateDecision.SkipSilently;

    public static string MissingConnectionStringMessage =>
        $"{RequireVariable}=1 but {ConnectionStringVariable} is not set. The integration suite would " +
        "have reported green having executed nothing. Set the connection string (a throwaway database — " +
        "the fixture drops and re-migrates it), or unset " + RequireVariable + " to allow the silent local skip.";

    public static string UnreachableMessage(Exception cause) =>
        $"{RequireVariable}=1 and {ConnectionStringVariable} is set, but the database could not be " +
        $"prepared: {cause.Message}. Failing loudly rather than skipping — see PostgresFixtureGate.";
}
