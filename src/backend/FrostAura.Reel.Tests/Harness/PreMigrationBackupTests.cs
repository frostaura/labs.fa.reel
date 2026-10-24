using FrostAura.Reel.Api;
using FrostAura.Reel.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// The API side of the pre-migration dump handshake (K-1). The sidecar side is the shell script in
/// <c>deploy/backup/reel-backup.sh</c>; here a test plays the sidecar by writing its answer files.
/// </summary>
public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "reel-premigration-" + Guid.NewGuid().ToString("N"));

    public PreMigrationBackupTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>Plays the sidecar: waits for the request, checks it, answers with <paramref name="suffix"/>.</summary>
    private async Task<string> AnswerAsync(string suffix, string content)
    {
        for (var i = 0; i < 200; i++)
        {
            var request = Directory.GetFiles(directory, "*.request").SingleOrDefault();
            if (request is not null)
            {
                var body = await File.ReadAllTextAsync(request);
                var id = Path.GetFileNameWithoutExtension(request);
                await File.WriteAllTextAsync(Path.Combine(directory, id + suffix), content);
                File.Delete(request);
                return body;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The API never wrote a request.");
    }

    [Fact]
    public async Task A_done_answer_returns_the_dump_and_clears_the_handshake()
    {
        var sidecar = AnswerAsync(".done", "/backups/pre-migration/reel-x.dump\n");

        var answer = await PreMigrationBackup.RequestAsync(directory, 3, "20261101000000_M1", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), default);

        Assert.Equal(new PreMigrationBackup.Answer(PreMigrationBackup.Outcome.Done, "/backups/pre-migration/reel-x.dump"), answer);
        Assert.Equal("pending=3\nlatest=20261101000000_M1\n", await sidecar);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task An_answer_this_process_may_not_delete_still_counts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The non-root API case: the sidecar (root) answered in a directory this user cannot delete
        // from. Throwing here would exit after a successful dump and restart into another, forever.
        var sidecar = Task.Run(async () =>
        {
            var body = await AnswerAsync(".done", "/backups/pre-migration/reel-y.dump\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            return body;
        });

        // A 500 ms poll: the API checks once at once (nothing there yet), and by its second look the
        // stand-in sidecar (10 ms polling) has answered and locked the directory.
        var answer = await PreMigrationBackup.RequestAsync(directory, 2, "M", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), default);
        await sidecar;

        Assert.Equal(new PreMigrationBackup.Answer(PreMigrationBackup.Outcome.Done, "/backups/pre-migration/reel-y.dump"), answer);
        // Non-vacuous where it can be: as a non-root user the answer really could not be deleted.
        if (Environment.UserName != "root")
        {
            Assert.Single(Directory.GetFiles(directory, "*.done"));
        }
    }

    [Fact]
    public async Task A_failed_answer_is_reported_with_its_reason()
    {
        var sidecar = AnswerAsync(".failed", "pg_dump failed; see the backup container log");

        var answer = await PreMigrationBackup.RequestAsync(directory, 1, "M", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), default);
        await sidecar;

        Assert.Equal(PreMigrationBackup.Outcome.Failed, answer.Outcome);
        Assert.Equal("pg_dump failed; see the backup container log", answer.Detail);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task No_answer_times_out_and_withdraws_the_request()
    {
        var answer = await PreMigrationBackup.RequestAsync(directory, 1, "M", TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20), default);

        Assert.Equal(PreMigrationBackup.Outcome.TimedOut, answer.Outcome);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task A_missing_directory_fails_without_waiting()
    {
        var answer = await PreMigrationBackup.RequestAsync(Path.Combine(directory, "absent"), 1, "M", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), default);

        Assert.Equal(PreMigrationBackup.Outcome.Failed, answer.Outcome);
        Assert.Contains("does not exist", answer.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_default_to_required_with_a_thirty_minute_wait_and_no_directory()
    {
        var options = ValidatedOptions.From<PreMigrationBackupOptions>(new ConfigurationBuilder().Build());

        Assert.Empty(options.Problems);
        Assert.Null(options.RequestDirectory);
        Assert.Equal(1800, options.TimeoutSeconds);
        Assert.True(options.Required);
    }

    [Theory]
    [InlineData("BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS", "10")]
    [InlineData("BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS", "1801")]
    [InlineData("BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS", "7200")]
    [InlineData("BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS", "forever")]
    [InlineData("BACKUP_PRE_MIGRATION_REQUIRED", "yes")]
    public void A_wrong_value_is_a_startup_problem(string key, string value)
    {
        var options = ValidatedOptions.From<PreMigrationBackupOptions>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());

        Assert.StartsWith(key + " must be", Assert.Single(options.Problems), StringComparison.Ordinal);
    }
}
