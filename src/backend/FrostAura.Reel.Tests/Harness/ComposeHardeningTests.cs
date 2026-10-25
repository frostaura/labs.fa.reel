using System.Text.RegularExpressions;
using FrostAura.Reel.Api;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Three properties every descriptor must keep, each with an incident behind it:
/// <list type="bullet">
/// <item><b>Loopback by default (J-1).</b> The dev gateway published on 0.0.0.0 with the host
/// firewall off, and the LAN got a Founder session. Every published port binds to a
/// <c>*_BIND</c> variable whose default is 127.0.0.1.</item>
/// <item><b>Rotated logs (J*2).</b> The json-file default kept about 30 minutes of the backend's
/// log. Every service logs through the shared <c>local</c>-driver anchor.</item>
/// <item><b>Backups (K-1).</b> There were none. Every deployable descriptor runs the backup
/// sidecar, and the backend shares the handshake volume it asks for its pre-migration dump on.</item>
/// </list>
/// </summary>
public partial class ComposeHardeningTests
{
    private static readonly string[] Descriptors = ["docker-compose.yml", "docker-compose.prod.yml", "deploy/portainer-stack.yml"];

    public static TheoryData<string> AllDescriptors()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in Descriptors.Append("docker-compose.eval.yml").Where(ComposeDescriptor.Exists))
        {
            data.Add(descriptor);
        }

        return data;
    }

    public static TheoryData<string> DeployableDescriptors() => [.. Descriptors];

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Every_published_port_binds_to_loopback_unless_opted_in(string descriptor)
    {
        var compose = ComposeDescriptor.Load(descriptor);
        var published = compose.Services.SelectMany(service => compose.Ports(service).Select(port => (service, port))).ToList();

        var open = published.Where(p => !LoopbackDefaultBind().IsMatch(p.port)).Select(p => $"{p.service}: {p.port}").ToList();

        Assert.True(open.Count == 0, $"{descriptor}: port(s) published without a loopback-default bind address: {string.Join("; ", open)}");
    }

    [Fact]
    public void The_dev_stack_publishes_its_gateway_and_database_on_loopback_by_default()
    {
        // Non-vacuous: the dev stack DOES publish, and these are the two the LAN reached.
        var compose = ComposeDescriptor.Load("docker-compose.yml");

        Assert.Equal(["${GATEWAY_BIND:-127.0.0.1}:${GATEWAY_PORT:-8090}:80"], compose.Ports("gateway"));
        Assert.Equal(["${POSTGRES_BIND:-127.0.0.1}:${POSTGRES_HOST_PORT:-5433}:5432"], compose.Ports("postgres"));
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Every_service_logs_through_the_rotated_local_driver(string descriptor)
    {
        var compose = ComposeDescriptor.Load(descriptor);

        Assert.Contains(
            "x-logging: &default-logging\n  driver: local\n  options:\n    max-size: \"50m\"\n    max-file: \"10\"\n",
            compose.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        var unrotated = compose.Services
            .Where(service => !compose.ServiceBlock(service).Contains("    logging: *default-logging"))
            .ToList();

        Assert.True(compose.Services.Count >= 4, $"{descriptor}: expected the stack's services; found {compose.Services.Count}.");
        Assert.True(unrotated.Count == 0, $"{descriptor}: service(s) without rotated logging: {string.Join(", ", unrotated)}");
    }

    [Theory]
    [MemberData(nameof(DeployableDescriptors))]
    public void Every_deployable_stack_runs_the_backup_sidecar_against_its_own_database(string descriptor)
    {
        var compose = ComposeDescriptor.Load(descriptor);

        Assert.Contains("backup", compose.Services);
        var block = compose.ServiceBlock("backup");
        Assert.Contains("    image: pgvector/pgvector:pg16", block);
        Assert.Contains("    entrypoint: [ \"/bin/sh\", \"/usr/local/bin/reel-backup.sh\" ]", block);
        Assert.Contains("      - backup_requests:/backup-requests", block);
        Assert.Contains(block, l => l.StartsWith("      - ${BACKUP_HOST_DIR:", StringComparison.Ordinal) && l.EndsWith("}:/backups", StringComparison.Ordinal));

        var environment = compose.Environment("backup");
        Assert.Equal("postgres", environment["PGHOST"]);
        Assert.Equal("${POSTGRES_DB:-reel}", environment["PGDATABASE"]);
        Assert.Equal("${BACKUP_KEEP_DAILY:-14}", environment["BACKUP_KEEP_DAILY"]);
        Assert.Equal("${BACKUP_KEEP_WEEKLY:-8}", environment["BACKUP_KEEP_WEEKLY"]);
    }

    [Theory]
    [MemberData(nameof(DeployableDescriptors))]
    public void The_backend_shares_the_handshake_volume_and_asks_before_migrating(string descriptor)
    {
        var compose = ComposeDescriptor.Load(descriptor);
        var backend = compose.ServiceBlock("backend");
        var environment = compose.Environment("backend");

        Assert.Contains("      - backup_requests:/backup-requests", backend);
        Assert.Equal("/backup-requests", environment["BACKUP_REQUEST_DIR"]);
        Assert.Equal("${BACKUP_PRE_MIGRATION_REQUIRED:-true}", environment["BACKUP_PRE_MIGRATION_REQUIRED"]);
        Assert.Contains(compose.TopLevelBlock("volumes"), l => l == "  backup_requests:");
    }

    [Theory]
    [MemberData(nameof(DeployableDescriptors))]
    public void The_backend_healthcheck_outlasts_the_longest_pre_migration_dump_wait(string descriptor)
    {
        // The listener opens only after the dump and the migrations. If the healthcheck gives up
        // first, compose marks the backend unhealthy and never starts the gateway (WS-8 review D2).
        var block = ComposeDescriptor.Load(descriptor).ServiceBlock("backend");
        var line = Assert.Single(block, l => l.StartsWith("      start_period: ", StringComparison.Ordinal));

        var startPeriod = ParseDuration(line["      start_period: ".Length..].Trim());

        Assert.True(
            startPeriod >= TimeSpan.FromSeconds(PreMigrationBackupOptions.MaxTimeoutSeconds) + MigrationAllowance,
            $"{descriptor}: backend start_period {startPeriod} must be at least the longest dump wait "
            + $"({PreMigrationBackupOptions.MaxTimeoutSeconds}s) plus {MigrationAllowance.TotalMinutes} minutes for the migrations.");
    }

    [Theory]
    [InlineData("docker-compose.prod.yml")]
    [InlineData("deploy/portainer-stack.yml")]
    public void Production_dumps_go_to_a_required_host_directory_never_a_stack_volume(string descriptor)
    {
        // A volume of the stack shares fate with the database: `down -v`, or removing the stack with
        // its volumes, deletes both together (WS-8 review D3).
        var compose = ComposeDescriptor.Load(descriptor);

        Assert.Contains(compose.ServiceBlock("backup"), l => l.StartsWith("      - ${BACKUP_HOST_DIR:?", StringComparison.Ordinal));
        Assert.DoesNotContain(compose.TopLevelBlock("volumes"), l => l == "  reel_backups:");
    }

    [Fact]
    public void Dev_dumps_default_to_a_directory_outside_the_repository()
    {
        // ./.backups put 350 MB dumps of the founder's data into the working tree, untracked and
        // un-ignored (WS-8 review defect 3).
        var mount = Assert.Single(
            ComposeDescriptor.Load("docker-compose.yml").ServiceBlock("backup"),
            l => l.StartsWith("      - ${BACKUP_HOST_DIR:-", StringComparison.Ordinal));

        Assert.Equal("      - ${BACKUP_HOST_DIR:-${HOME}/.reel/backups}:/backups", mount);
    }

    [Theory]
    [InlineData("${GATEWAY_BIND:-127.0.0.1}:${GATEWAY_PORT:-8090}:80", true)]
    [InlineData("127.0.0.1:5434:5432", true)]
    [InlineData("\"127.0.0.1:5434:5432\"", true)]
    [InlineData("${GATEWAY_BIND:-0.0.0.0}:8090:80", false)]
    [InlineData("0.0.0.0:5434:5432", false)]
    [InlineData("5434:5432", false)]
    [InlineData("${GATEWAY_PORT:-8090}:80", false)]
    public void A_port_is_loopback_by_default_only_when_its_address_says_so(string port, bool loopback)
    {
        // WS-7's eval file publishes a literal 127.0.0.1 address; that is as closed as a *_BIND default.
        var compose = ComposeDescriptor.Parse("x.yml", $"services:\n  db:\n    ports:\n      - {port}\n");

        Assert.Equal(loopback, LoopbackDefaultBind().IsMatch(Assert.Single(compose.Ports("db"))));
    }

    private static readonly TimeSpan MigrationAllowance = TimeSpan.FromMinutes(10);

    private static TimeSpan ParseDuration(string value)
    {
        var match = ComposeDuration().Match(value);
        Assert.True(match.Success, $"Unrecognised compose duration '{value}'.");
        var amount = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "h" => TimeSpan.FromHours(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromSeconds(amount),
        };
    }

    [GeneratedRegex(@"^([0-9]+)(h|m|s)$")]
    private static partial Regex ComposeDuration();

    [GeneratedRegex(@"^(\$\{[A-Z_]+_BIND:-127\.0\.0\.1\}|127\.0\.0\.1):")]
    private static partial Regex LoopbackDefaultBind();
}

/// <summary>
/// The backup script ships three times — the canonical file the dev stack mounts, and two inline
/// copies, because a Portainer stack cannot mount a file out of a repository it never cloned. Same
/// discipline as <see cref="NginxDescriptorParityTests"/>: one canonical copy plus this check.
/// </summary>
public class BackupDescriptorParityTests
{
    [Theory]
    [InlineData("docker-compose.prod.yml")]
    [InlineData("deploy/portainer-stack.yml")]
    public void The_inline_script_is_the_canonical_script(string descriptor)
    {
        var canonical = File.ReadAllText(Path.Combine(ComposeDescriptor.RepositoryRoot(), "deploy", "backup", "reel-backup.sh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var inline = ComposeDescriptor.Load(descriptor).InlineConfig("reel_backup_script");

        Assert.NotNull(inline);
        // Compose interpolates the inline copy, so every $ in it is written $$.
        Assert.Equal(canonical, inline.Replace("$$", "$", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"(?<!\$)\$(?!\$)", inline);
    }

    [Fact]
    public void The_dev_stack_mounts_the_canonical_script()
    {
        Assert.Contains(
            "      - ./deploy/backup/reel-backup.sh:/usr/local/bin/reel-backup.sh:ro",
            ComposeDescriptor.Load("docker-compose.yml").ServiceBlock("backup"));
    }
}
