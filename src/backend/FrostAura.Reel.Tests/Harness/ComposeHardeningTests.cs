using System.Text.RegularExpressions;
using FrostAura.Reel.Api;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Three properties every descriptor must keep, each with an incident behind it:
/// <list type="bullet">
/// <item><b>Loopback by default (J-1).</b> The dev gateway published on 0.0.0.0 with the host
/// firewall off, and the LAN got a Founder session. Every published port binds to a
/// <c>*_BIND</c> variable whose default is 127.0.0.1.</item>
/// <item><b>Rotated logs (J*2, R1-37).</b> The json-file default kept about 30 minutes of the
/// backend's log. Every service logs through the shared <c>local</c>-driver anchor, and every
/// deployable stack's rotation holds at least 72 hours at the idle-volume ceiling.</item>
/// <item><b>Backups (K-1).</b> There were none. Every deployable descriptor runs the backup
/// sidecar, and the backend shares the handshake volume it asks for its pre-migration dump on.</item>
/// </list>
/// </summary>
public partial class ComposeHardeningTests
{
    private static readonly string[] Descriptors = ["docker-compose.yml", "docker-compose.prod.yml"];

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

        Assert.NotNull(LoggingAnchor(compose));

        var unrotated = compose.Services
            .Where(service => !compose.ServiceBlock(service).Contains("    logging: *default-logging"))
            .ToList();

        // Non-vacuous: the eval stack is a Postgres and an API; every other descriptor is the full stack.
        var expected = descriptor == "docker-compose.eval.yml" ? 2 : 4;
        Assert.True(compose.Services.Count >= expected, $"{descriptor}: expected the stack's services; found {compose.Services.Count}.");
        Assert.True(unrotated.Count == 0, $"{descriptor}: service(s) without rotated logging: {string.Join(", ", unrotated)}");
    }

    /// <summary>The program's floor on how far back a diagnosis can look (WS-6 acceptance).</summary>
    internal const double RetentionHoursFloor = 72;

    /// <summary>
    /// The per-container log volume the rotation is sized for: the program's idle-volume ceiling, not
    /// a measurement. Measured 2026-10-25 on slot ws8 (appsettings.json logging: JSON console with
    /// scopes, Microsoft.* at Warning): 8.6 KB of startup lines, then 0 bytes over 602 s idle. That
    /// slot ran the job runner but had the eight timed background services off, so the ceiling, not
    /// the measurement, is what the rotation is sized for.
    /// </summary>
    internal const double SizedForKiBPerMinute = 200;

    [Theory]
    [MemberData(nameof(DeployableDescriptors))]
    public void Every_deployable_stack_keeps_at_least_72_hours_of_log_at_the_volume_ceiling(string descriptor)
    {
        // R1-37. Counted uncompressed, so the `local` driver's compression of rotated files is margin.
        var (sizeMiB, files) = LoggingAnchor(ComposeDescriptor.Load(descriptor))!.Value;
        var hours = RetainedHours(sizeMiB, files, SizedForKiBPerMinute);

        Assert.True(
            hours >= RetentionHoursFloor,
            $"{descriptor}: {files} x {sizeMiB} MB keeps {hours:F1} h at {SizedForKiBPerMinute} KB/min; at least {RetentionHoursFloor} h is required.");
    }

    [Fact]
    public void The_retention_arithmetic_rejects_the_rotation_it_replaced()
    {
        // Non-vacuous: the previous 10 x 50 MB held under 42 h at the ceiling.
        Assert.True(RetainedHours(50, 10, SizedForKiBPerMinute) < RetentionHoursFloor);
        Assert.Equal(85.3, RetainedHours(100, 10, SizedForKiBPerMinute), precision: 1);

        // And the anchor reader refuses a block it cannot size, rather than passing it.
        Assert.Null(LoggingAnchor(ComposeDescriptor.Parse("x.yml", "x-logging: &default-logging\n  driver: json-file\n  options:\n    max-size: \"100m\"\n    max-file: \"10\"\nservices:\n")));
        Assert.Null(LoggingAnchor(ComposeDescriptor.Parse("x.yml", "x-logging: &default-logging\n  driver: local\n  options:\n    max-size: \"1g\"\n    max-file: \"10\"\nservices:\n")));
        Assert.Equal((100, 10), LoggingAnchor(ComposeDescriptor.Parse("x.yml", "x-logging: &default-logging\n  driver: local\n  options:\n    max-size: \"100m\"\n    max-file: \"10\"\nservices:\n")));
    }

    /// <summary>Hours of log a <paramref name="files"/> x <paramref name="sizeMiB"/> rotation holds at a steady volume.</summary>
    internal static double RetainedHours(int sizeMiB, int files, double kiBPerMinute) =>
        sizeMiB * 1024.0 * files / kiBPerMinute / 60.0;

    /// <summary>The shared anchor's size (MB) and file count, or null when it is not a sizeable <c>local</c>-driver rotation.</summary>
    private static (int SizeMiB, int Files)? LoggingAnchor(ComposeDescriptor compose)
    {
        var match = LoggingAnchorBlock().Match(compose.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        return match.Success
            ? (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    [GeneratedRegex("^x-logging: &default-logging\n  driver: local\n  options:\n    max-size: \"([0-9]+)m\"\n    max-file: \"([0-9]+)\"\n", RegexOptions.Multiline)]
    private static partial Regex LoggingAnchorBlock();

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
    public void Production_dumps_go_to_a_required_host_directory_never_a_stack_volume(string descriptor)
    {
        // A volume of the stack shares fate with the database: `down -v`, or removing the stack with
        // its volumes, deletes both together (WS-8 review D3).
        var compose = ComposeDescriptor.Load(descriptor);

        Assert.Contains(compose.ServiceBlock("backup"), l => l.StartsWith("      - ${BACKUP_HOST_DIR:?", StringComparison.Ordinal));
        Assert.DoesNotContain(compose.TopLevelBlock("volumes"), l => l == "  reel_backups:");
    }

    [Theory]
    [MemberData(nameof(DeployableDescriptors))]
    public void The_backend_gets_45_seconds_to_stop(string descriptor)
    {
        // K-12: Docker's 10 s default SIGKILLs a streaming Ask turn and the job runner's claim
        // mid-write on every redeploy.
        Assert.Contains("    stop_grace_period: 45s", ComposeDescriptor.Load(descriptor).ServiceBlock("backend"));
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("frontend")]
    [InlineData("gateway")]
    public void Production_images_are_pinned_by_REEL_TAG(string service)
    {
        // K-9: `:latest` names whatever was pushed last, so neither a rollback nor "what is running"
        // had an answer. The deploy job sets REEL_TAG=sha-<short>; a rollback sets it back.
        Assert.Contains(
            $"    image: ${{DOCKERHUB_USERNAME:-frostaura}}/reel-{service}:${{REEL_TAG:-latest}}",
            ComposeDescriptor.Load("docker-compose.prod.yml").ServiceBlock(service));
    }

    [Fact]
    public void Production_publishes_no_host_port_and_its_only_way_in_is_the_in_stack_tunnel()
    {
        // One exposed surface: the gateway, reached only by the cloudflared service of this same stack.
        // A published port beside it is a second surface nobody is watching (and, on 0.0.0.0, J-1).
        var compose = ComposeDescriptor.Load("docker-compose.prod.yml");

        Assert.Equal(["postgres", "backend", "backup", "frontend", "gateway", "cloudflared"], compose.Services);
        Assert.All(compose.Services, service =>
        {
            Assert.Empty(compose.Ports(service));
            Assert.DoesNotContain(compose.ServiceBlock(service), l => l.Trim() is "ports:" or "network_mode: host");
        });

        var tunnel = compose.ServiceBlock("cloudflared");
        // Pinned to a released version; `latest` would change the public edge under an unchanged file.
        var image = Assert.Single(tunnel, l => l.StartsWith("    image: ", StringComparison.Ordinal));
        Assert.Matches(@"^    image: cloudflare/cloudflared:[0-9]{4}\.[0-9]+\.[0-9]+$", image);
        Assert.Contains("    command: [ \"tunnel\", \"--no-autoupdate\", \"run\" ]", tunnel);
        Assert.StartsWith("${CLOUDFLARE_TUNNEL_TOKEN:?", compose.Environment("cloudflared")["TUNNEL_TOKEN"], StringComparison.Ordinal);
        Assert.Contains("      - gateway", tunnel);
    }

    [Fact]
    public void Production_believes_forwarded_headers_only_from_its_own_pinned_bridge()
    {
        var compose = ComposeDescriptor.Load("docker-compose.prod.yml");
        var subnet = Assert.Single(
            compose.TopLevelBlock("networks"),
            l => l.StartsWith("        - subnet: ", StringComparison.Ordinal))["        - subnet: ".Length..].Trim();
        var network = System.Net.IPNetwork.Parse(subnet);

        // Outside Docker's default address pools, so Docker can never allocate it to anything else.
        foreach (var pool in new[] { "172.16.0.0/12", "192.168.0.0/16" })
        {
            var p = System.Net.IPNetwork.Parse(pool);
            Assert.False(p.Contains(network.BaseAddress) || network.Contains(p.BaseAddress), $"{subnet} overlaps Docker's default pool {pool}.");
        }

        // And it is exactly what the API trusts: no loopback, no Docker-wide range.
        Assert.Equal($"\"${{TRUSTED_PROXY_NETWORKS:-{subnet}}}\"", compose.Environment("backend")["TRUSTED_PROXY_NETWORKS"]);
    }

    [Fact]
    public void The_images_never_run_as_root_and_never_resolve_around_the_lockfile()
    {
        var root = ComposeDescriptor.RepositoryRoot();
        var backend = File.ReadAllLines(Path.Combine(root, "src", "backend", "Dockerfile"));
        var frontend = File.ReadAllText(Path.Combine(root, "src", "frontend", "Dockerfile"));

        // K-12: the last USER of the runtime stage is the unprivileged app user.
        Assert.Equal("USER $APP_UID", backend.Last(l => l.StartsWith("USER ", StringComparison.Ordinal)));
        // K-13: cross-published for the target architecture, restore cached in its own layer.
        Assert.Contains(backend, l => l.Contains("-a \"$TARGETARCH\"", StringComparison.Ordinal) && l.Contains("dotnet restore", StringComparison.Ordinal));
        // K-13: no `npm ci || npm install` — a lockfile drift fails the build instead of being resolved around.
        Assert.DoesNotContain("||", frontend.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Aggregate(string.Empty, string.Concat), StringComparison.Ordinal);
        Assert.Contains("RUN npm ci --no-audit --no-fund\n", frontend, StringComparison.Ordinal);
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
/// The backup script ships twice — the canonical file the dev stack mounts, and the inline copy in
/// the production descriptor, because a Portainer stack cannot mount a file out of a repository it
/// never cloned. Same
/// discipline as <see cref="NginxDescriptorParityTests"/>: one canonical copy plus this check.
/// </summary>
public class BackupDescriptorParityTests
{
    [Theory]
    [InlineData("docker-compose.prod.yml")]
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
