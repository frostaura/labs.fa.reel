using System.Collections.Concurrent;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The real <see cref="JobRunnerService"/> on a real database, wired the way the API wires it
/// (scoped context and account context, handlers resolved per execution) with test handlers in place
/// of the production ones. Tests drive its loop with StartAsync/StopAsync, or a single execution
/// through <see cref="JobRunnerService.RunAsync"/> — never a copy of its logic (J-6).
/// </summary>
internal sealed class JobRunnerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private JobRunnerHarness(ServiceProvider services, JobRunnerService runner, RecordingEventHub events)
    {
        _services = services;
        Runner = runner;
        Events = events;
    }

    public JobRunnerService Runner { get; }

    public RecordingEventHub Events { get; }

    public IServiceProvider Services => _services;

    public static JobRunnerHarness Create(
        string connectionString,
        JobRunnerOptions options,
        Action<IServiceCollection>? configure = null,
        ILogger<JobRunnerService>? logger = null)
    {
        var events = new RecordingEventHub();
        var services = new ServiceCollection();
        services.AddScoped<IAccountContext, AccountContext>();
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        services.AddSingleton<IPipelineEventHub>(events);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(validateScopes: true);
        var runner = new JobRunnerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            logger ?? NullLogger<JobRunnerService>.Instance,
            options);
        return new JobRunnerHarness(provider, runner, events);
    }

    /// <summary>Runner options shrunk so the loop can be driven in seconds; deadlines stay generous unless a test sets them.</summary>
    public static JobRunnerOptions FastOptions() => JobRunnerOptions.Default with
    {
        PollInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        ShutdownGrace = TimeSpan.FromSeconds(5),
        OverrunGrace = TimeSpan.FromSeconds(5),
    };

    public async ValueTask DisposeAsync()
    {
        Runner.Dispose();
        await _services.DisposeAsync();
    }
}

/// <summary>A handler whose behaviour the test scripts; it counts executions and how many run at once.</summary>
internal sealed class ScriptedHandler(JobKind kind, Func<SyncJob, int, CancellationToken, Task> body) : IJobHandler
{
    private int _executions;
    private int _running;
    private int _maxRunning;

    public JobKind Kind => kind;

    public int Executions => Volatile.Read(ref _executions);

    public int Running => Volatile.Read(ref _running);

    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var execution = Interlocked.Increment(ref _executions);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
        {
        }

        try
        {
            await body(job, execution, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public static ScriptedHandler Succeeds(JobKind kind) => new(kind, (_, _, _) => Task.CompletedTask);

    /// <summary>Holds its slot until the token is cancelled, then throws like a well-behaved handler.</summary>
    public static ScriptedHandler Blocks(JobKind kind) => new(kind, (_, _, ct) => Task.Delay(Timeout.Infinite, ct));
}

/// <summary>
/// A handler that works on the execution's own scoped context, the way production handlers do —
/// so its saves of the tracked job go through the same concurrency tokens theirs do.
/// </summary>
internal sealed class ContextHandler(JobKind kind, ReelDbContext db, Func<SyncJob, ReelDbContext, CancellationToken, Task> body) : IJobHandler
{
    public JobKind Kind => kind;

    public Task ExecuteAsync(SyncJob job, CancellationToken ct) => body(job, db, ct);

    /// <summary>Registers a scoped <see cref="ContextHandler"/> running <paramref name="body"/>.</summary>
    public static Action<IServiceCollection> For(JobKind kind, Func<SyncJob, ReelDbContext, CancellationToken, Task> body) =>
        services => services.AddScoped<IJobHandler>(sp => new ContextHandler(kind, sp.GetRequiredService<ReelDbContext>(), body));
}

/// <summary>Keeps every log line the runner writes, for tests that assert on what it reported.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class RecordingEventHub : IPipelineEventHub
{
    public ConcurrentQueue<(Guid AccountId, string Type)> Published { get; } = new();

    public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) =>
        Published.Enqueue((accountId, type));

    public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
        Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>A provider-health reader the test flips between readings.</summary>
internal sealed class SwitchableProviderHealthReader : IProviderHealthReader
{
    public ProviderHealthSnapshot Snapshot { get; set; } = new(ProviderHealthState.Healthy, DateTime.UtcNow, []);

    public ProviderHealthSnapshot Current() => Snapshot;
}

/// <summary>
/// A database of the runner tests' own (<c>&lt;test db&gt;_runner</c>), because the real loop claims
/// every Pending row it is allowed to and the shared integration database holds other tests' jobs.
/// </summary>
public sealed class JobRunnerDatabaseFixture : IAsyncLifetime
{
    private readonly PostgresFixture _gate = new();

    public bool Available { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Same gate as every integration collection: silent without a database, loud when one was demanded.
        switch (PostgresFixtureGate.Decide(_gate.ConnectionString, Environment.GetEnvironmentVariable(PostgresFixtureGate.RequireVariable)))
        {
            case IntegrationGateDecision.Fail:
                throw new InvalidOperationException(PostgresFixtureGate.MissingConnectionStringMessage);
            case IntegrationGateDecision.SkipSilently:
                return;
        }

        var builder = new NpgsqlConnectionStringBuilder(_gate.ConnectionString);
        builder.Database += "_runner";
        ConnectionString = builder.ConnectionString;

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        Available = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ReelDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
                .Options,
            new AccountContext());
}

[CollectionDefinition("job-runner")]
public class JobRunnerCollection : ICollectionFixture<JobRunnerDatabaseFixture>;
