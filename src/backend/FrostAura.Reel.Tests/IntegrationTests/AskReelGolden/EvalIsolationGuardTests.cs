namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// Test databases moved to the isolated eval Postgres (127.0.0.1:5434, program §5.0 rule 4). The fixture
/// drops the database it is handed, so the guard that keeps it off the founder's live Postgres (:5433)
/// and off the live database (`reel`, on any host) is asserted on its own, with the fixture varied
/// across every spelling of "this machine" and every casing of the name — a guard that only knows
/// "localhost" is one typo from a dropped live database.
/// </summary>
public class EvalIsolationGuardTests
{
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5433;Database=reel_test_ws7;Username=reel;Password=x")]
    [InlineData("Host=localhost;Port=5433;Database=reel_test_ws7;Username=reel;Password=x")]
    [InlineData("Host=LOCALHOST;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=::1;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("host=127.0.0.1;port=5433;database=reel_test_ws1;username=reel;password=x")]
    // Every other spelling of "this machine" the P0 review found walking past the guard.
    [InlineData("Host=host.docker.internal;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=0.0.0.0;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=127.0.0.2;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=127.255.255.254;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=::;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=reel.localhost;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=/tmp;Port=5433;Database=reel_test;Username=reel;Password=x")]
    // Multi-host: any one host on the live server is enough to refuse.
    [InlineData("Host=db.internal,127.0.0.1;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=localhost,127.0.0.1;Port=5433;Database=reel_test;Username=reel;Password=x")]
    // A per-host port wins over the string's Port.
    [InlineData("Host=localhost:5433;Port=5434;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=127.0.0.1:5434,127.0.0.1:5433;Database=reel_test;Username=reel;Password=x")]
    public void The_live_postgres_port_on_this_machine_is_refused(string connectionString)
    {
        var refusal = PostgresFixtureGate.LiveTargetRefusal(connectionString);

        Assert.NotNull(refusal);
        Assert.Contains("5434", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5434;Database=reel;Username=reel;Password=x")]
    [InlineData("Host=localhost;Port=5432;Database=REEL;Username=reel;Password=x")]
    [InlineData("Host=postgres;Port=5432;Database=Reel;Username=reel;Password=x")]
    // No Database: Npgsql opens the database named after the user, which here is the live one.
    [InlineData("Host=db.prod;Port=5432;Username=reel;Password=x")]
    [InlineData("Host=127.0.0.1;Port=5434;Username=REEL;Password=x")]
    public void The_live_database_name_is_refused_on_any_host_and_port(string connectionString)
    {
        var refusal = PostgresFixtureGate.LiveTargetRefusal(connectionString);

        Assert.NotNull(refusal);
        Assert.Contains("'reel'", refusal, StringComparison.Ordinal);
    }

    [Theory]
    // The eval Postgres, where every workstream's test database now lives.
    [InlineData("Host=127.0.0.1;Port=5434;Database=reel_test_ws7;Username=reel;Password=reel_eval")]
    // CI's pgvector service container.
    [InlineData("Host=localhost;Port=5432;Database=reel_test;Username=reel;Password=reel_test_pw")]
    // A database whose name merely starts with "reel" is not the live one.
    [InlineData("Host=127.0.0.1;Port=5434;Database=reel_eval;Username=reel;Password=x")]
    // Port 5433 on another host is somebody else's server, not the founder's laptop.
    [InlineData("Host=db.internal;Port=5433;Database=reel_test;Username=reel;Password=x")]
    [InlineData("Host=203.0.113.7;Port=5433;Database=reel_test;Username=reel;Password=x")]
    // No Database, but the user is not the live database's name.
    [InlineData("Host=127.0.0.1;Port=5434;Username=reel_test_ws7;Password=x")]
    // Multi-host entirely on the eval server.
    [InlineData("Host=localhost,127.0.0.1;Port=5434;Database=reel_test;Username=reel;Password=x")]
    public void Throwaway_databases_off_the_live_server_are_allowed(string connectionString)
    {
        Assert.Null(PostgresFixtureGate.LiveTargetRefusal(connectionString));
    }

    [Fact]
    public void This_machines_own_name_and_interface_addresses_count_as_this_machine()
    {
        var name = System.Net.Dns.GetHostName();
        Assert.NotNull(PostgresFixtureGate.LiveTargetRefusal($"Host={name};Port=5433;Database=reel_test;Username=reel;Password=x"));

        // A LAN address of this laptop reaches the live container's published port too.
        var own = PostgresFixtureGate.OwnAddresses()
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
        if (own is not null)
        {
            Assert.NotNull(PostgresFixtureGate.LiveTargetRefusal($"Host={own};Port=5433;Database=reel_test;Username=reel;Password=x"));
        }

        Assert.True(PostgresFixtureGate.IsThisMachine("127.0.0.1"));
        Assert.False(PostgresFixtureGate.IsThisMachine("203.0.113.7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Unparseable: Npgsql rejects it loudly later; the guard cannot name a target in it.
    [InlineData("this is not a connection string")]
    public void Absent_or_unparseable_strings_are_left_to_the_gate_and_to_Npgsql(string? connectionString)
    {
        Assert.Null(PostgresFixtureGate.LiveTargetRefusal(connectionString));
    }
}
