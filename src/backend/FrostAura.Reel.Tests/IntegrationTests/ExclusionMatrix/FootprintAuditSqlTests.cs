using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Tenancy;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>
/// Pins <c>eval/surfaces/footprint-audit.sql</c> — the timestamp-aware zero-footprint audit of the
/// exposure log (WS-7 task 7) — on a fixture where every lock starts at a known moment and the account is
/// served each title once before and once after it. Exactly the "after" exposures are leaks: a lock is
/// never applied backwards (the matrix's late cells are the live half of the same question), a revoked
/// reaction stops binding when it is revoked, and a rule that held a title out still counts when the
/// title is watched later (the audit reads rules.sql without its footprint filter).
/// </summary>
[Collection("postgres")]
public class FootprintAuditSqlTests(PostgresFixture fixture)
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Only_exposures_served_after_their_lock_started_are_leaks()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync();
        var horror = await TitleAsync(genres: ["horror"]);
        var french = await TitleAsync(language: "fr");
        var watched = await TitleAsync();
        var rated = await TitleAsync();
        var dismissed = await TitleAsync();
        var watchedLater = await TitleAsync(genres: ["horror"]);
        var clean = await TitleAsync();

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // The rule starts at T0 + 1 day; English-only (no language row) has bound since the account
            // was created, at T0.
            db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = account, Kind = FilterKind.ExcludeGenre, Value = "horror", CreatedAt = T0.AddDays(1) });
            db.WatchedTitles.Add(new WatchedTitle { Id = Guid.NewGuid(), AccountId = account, TitleId = watched, Plays = 1, IsFullyWatched = true, LastWatchedAt = T0.AddDays(2), FirstSyncedAt = T0.AddDays(2), UpdatedAt = T0.AddDays(2) });
            db.WatchedTitles.Add(new WatchedTitle { Id = Guid.NewGuid(), AccountId = account, TitleId = watchedLater, Plays = 1, IsFullyWatched = true, LastWatchedAt = T0.AddDays(9), FirstSyncedAt = T0.AddDays(9), UpdatedAt = T0.AddDays(9) });
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account,
                TitleId = rated,
                Rating = 7,
                SubjectType = RatingSubjectType.Movie,
                RatedAt = T0.AddDays(8),
                FirstRatedAt = T0.AddDays(3),
                SyncedAt = T0.AddDays(8),
            });
            // Not interested from day 4, revoked on day 6.
            db.UserTitleReactions.Add(new UserTitleReaction { Id = Guid.NewGuid(), AccountId = account, TitleId = dismissed, Kind = ReactionKind.NotInterested, CreatedAt = T0.AddDays(4), RevokedAt = T0.AddDays(6) });
            await db.SaveChangesAsync();
        }

        await ExposeAsync(account, horror, T0.AddHours(12), T0.AddDays(1).AddHours(1));
        await ExposeAsync(account, french, T0.AddHours(1));
        await ExposeAsync(account, watched, T0.AddDays(1), T0.AddDays(2).AddHours(1));
        await ExposeAsync(account, rated, T0.AddDays(2), T0.AddDays(3).AddHours(1));
        await ExposeAsync(account, dismissed, T0.AddDays(3), T0.AddDays(5), T0.AddDays(7));
        // Held out by the rule from day 1, watched on day 9: the day-5 exposure is a rule leak.
        await ExposeAsync(account, watchedLater, T0.AddHours(6), T0.AddDays(5));
        await ExposeAsync(account, clean, T0.AddDays(5));

        var leaks = await AuditAsync(account, T0.AddDays(-1));

        Assert.Equal(
            new[]
            {
                "FeedHero|not-interested|1|0",
                "FeedHero|rated|1|0",
                "FeedHero|rule ExcludeGenre horror|2|1",
                "FeedHero|rule Language en|1|0",
                "FeedHero|watched|1|0",
            },
            leaks);

        // --since confines the audit: from day 4 on, only the day-5 exposures remain.
        Assert.Equal(new[] { "FeedHero|not-interested|1|0", "FeedHero|rule ExcludeGenre horror|1|1" }, await AuditAsync(account, T0.AddDays(4)));
    }

    /// <summary>The audit's rows as "surface|reason|exposures|more than a day late", in a stable order.</summary>
    private async Task<string[]> AuditAsync(Guid account, DateTime since)
    {
        var root = RepoRoot();
        var rules = await File.ReadAllTextAsync(Path.Combine(root, "eval", "askreel", "supply", "rules.sql"));
        const string marker = " WHERE NOT EXISTS (SELECT 1 FROM \"WatchedTitles\" w WHERE w.\"AccountId\" = :'account' AND w.\"TitleId\" = t.\"Id\")";
        var at = rules.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "supply/rules.sql no longer carries its footprint filter where scripts/eval/footprint-audit.mjs cuts it");
        var audit = await File.ReadAllTextAsync(Path.Combine(root, "eval", "surfaces", "footprint-audit.sql"));
        var sql = $"CREATE TEMP VIEW eval_rules_all AS {Bind(rules[..at], account, since)};\n{Bind(audit, account, since)}";

        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 9)
                {
                    rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt32(2)}|{reader.GetInt32(4)}");
                }
            }
        }
        while (await reader.NextResultAsync());

        return [.. rows.Order(StringComparer.Ordinal)];
    }

    /// <summary>psql's variables bound as literals; comment lines dropped.</summary>
    private static string Bind(string sql, Guid account, DateTime since) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            .Replace(":'account'", $"'{account}'", StringComparison.Ordinal)
            .Replace(":'since'", $"'{since:O}'", StringComparison.Ordinal)
            .Replace(":'probes'", "'{}'", StringComparison.Ordinal)
            .TrimEnd().TrimEnd(';');

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"footprint-audit-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = T0,
            LastSeenAt = T0,
            PipelineStageChangedAt = T0,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> TitleAsync(string[]? genres = null, string language = "en")
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"footprint-audit {Guid.NewGuid():N}",
            Language = language,
            Genres = genres ?? ["drama"],
            Keywords = [],
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 1_000,
            Year = 2010,
            TmdbPopularity = 0m,
            CreatedAt = T0,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task ExposeAsync(Guid account, Guid titleId, params DateTime[] at)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var when in at)
        {
            db.ExposureEvents.Add(new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = account,
                TitleId = titleId,
                OccurredAt = when,
                Surface = ExposureSurface.FeedHero,
                ModelArtifactId = Guid.NewGuid(),
                PredictedRating = 7m,
                WhySource = "Templater",
            });
        }

        await db.SaveChangesAsync();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "labs.fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("labs.fa.reel.slnx not found above the test assembly.");
    }
}
