using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Npgsql;

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

    /// <summary>The founder's LIVE Postgres publishes on this host port (docker-compose.yml).</summary>
    public const int LivePostgresPort = 5433;

    /// <summary>The founder's LIVE database. The fixture DROPS the database it is given.</summary>
    public const string LiveDatabaseName = "reel";

    /// <summary>
    /// Why a connection string must never reach the fixture, or null when it may. Test databases live
    /// on the isolated eval Postgres (127.0.0.1:5434) or CI's service container — never on the live
    /// Postgres, and never in the live database on any host. The fixture runs <c>EnsureDeleted</c>
    /// first, so the only safe answer to "is this the live database?" is checked before it connects.
    /// A string Npgsql cannot parse is left for Npgsql to reject loudly; it cannot name a target.
    ///
    /// The database checked is the one Npgsql will actually open: with no <c>Database</c> it defaults to
    /// the username, so <c>Username=reel</c> alone targets <c>reel</c>. Every host of a multi-host string
    /// is checked, with its own <c>host:port</c> when it carries one, and "this machine" covers every
    /// spelling that reaches the live container: the loopback range, the wildcard address, Docker's
    /// host aliases, a unix socket, the machine's own names and its own interface addresses.
    /// </summary>
    public static string? LiveTargetRefusal(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        NpgsqlConnectionStringBuilder target;
        try
        {
            target = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var database = string.IsNullOrEmpty(target.Database) ? target.Username : target.Database;
        if (string.Equals(database, LiveDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{ConnectionStringVariable} targets the database '{LiveDatabaseName}' (named, or defaulted from the " +
                "username when no Database is given), which is the founder's LIVE database. The fixture drops and " +
                "re-migrates the database it is given. Use a throwaway reel_test_* database on the eval Postgres " +
                "(127.0.0.1:5434).";
        }

        foreach (var (host, port) in Hosts(target))
        {
            if (port == LivePostgresPort && IsThisMachine(host))
            {
                return $"{ConnectionStringVariable} points at port {LivePostgresPort} on this machine ('{host}'), which is " +
                    "the founder's LIVE Postgres. Test databases live on the isolated eval Postgres (127.0.0.1:5434) — " +
                    "move the connection string there.";
            }
        }

        return null;
    }

    /// <summary>Every host Npgsql may try, each with the port it will use (its own, or the string's).</summary>
    private static IEnumerable<(string Host, int Port)> Hosts(NpgsqlConnectionStringBuilder target)
    {
        var hosts = string.IsNullOrWhiteSpace(target.Host) ? [string.Empty] : target.Host.Split(',');
        foreach (var raw in hosts)
        {
            var host = raw.Trim();
            var port = target.Port;
            // "[::1]:5433", "localhost:5433" — but a bare IPv6 literal ("::1") carries no port.
            var colon = host.LastIndexOf(':');
            var bracketed = host.StartsWith('[');
            if (colon > 0 && (bracketed ? host[colon - 1] == ']' : host.IndexOf(':') == colon)
                && int.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var own))
            {
                port = own;
                host = host[..colon];
            }

            yield return (host.Trim('[', ']'), port);
        }
    }

    /// <summary>True when <paramref name="host"/> reaches a server published on this machine.</summary>
    public static bool IsThisMachine(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith('/'))
        {
            return true;
        }

        var name = host.Trim().Trim('[', ']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gateway.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(name, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any)
                || OwnAddresses().Contains(address);
        }

        var machine = Dns.GetHostName();
        return name.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(machine.Split('.')[0], StringComparison.OrdinalIgnoreCase)
            || name.Equals($"{machine.Split('.')[0]}.local", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This machine's own interface addresses (a LAN IP of this laptop reaches the live container too).</summary>
    public static HashSet<IPAddress> OwnAddresses()
    {
        var own = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    own.Add(unicast.Address.IsIPv4MappedToIPv6 ? unicast.Address.MapToIPv4() : unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No interface list: the loopback, wildcard and name checks above still apply.
        }

        return own;
    }

    public static string MissingConnectionStringMessage =>
        $"{RequireVariable}=1 but {ConnectionStringVariable} is not set. The integration suite would " +
        "have reported green having executed nothing. Set the connection string (a throwaway database — " +
        "the fixture drops and re-migrates it), or unset " + RequireVariable + " to allow the silent local skip.";

    public static string UnreachableMessage(Exception cause) =>
        $"{RequireVariable}=1 and {ConnectionStringVariable} is set, but the database could not be " +
        $"prepared: {cause.Message}. Failing loudly rather than skipping — see PostgresFixtureGate.";
}
