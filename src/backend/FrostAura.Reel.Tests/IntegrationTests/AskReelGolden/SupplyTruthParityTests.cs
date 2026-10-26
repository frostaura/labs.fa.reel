using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// The golden eval holds a none-found reply's counts — "you've seen N", "M have no prediction yet",
/// "dropping X would add K" — to its OWN SQL statement of the supply (<c>eval/askreel/supply/*.sql</c>),
/// ±0 (IREV1-SYS-06, WS-2's acceptance). Two statements of one count are only useful while they agree, so
/// this pins the SQL to <see cref="SupplyReporter"/> on fixtures that put a title in every bucket and trip
/// every relaxation arm from both sides: a title failing one rule (a knockout), a title failing two (not
/// one), a seen title failing a rule (never a relaxation), a title failing a rule AND the maturity ceiling
/// (not unlocked by dropping the rule), the single-value allow-list, the vote floor on both crowd sources,
/// and the For-you bar at badge precision. If the reporter drifts, the eval would fail honest replies; if
/// the SQL drifts, it would pass dishonest ones.
/// </summary>
[Collection("postgres")]
public class SupplyTruthParityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Supply_truth_equals_the_reporter_on_every_bucket_and_every_knockout()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"goldensupply{Guid.NewGuid():N}";
        var account = await SeedAccountAsync(null,
            (FilterKind.ExcludeGenre, "comedy"),
            (FilterKind.ExcludeGenre, "horror"),
            (FilterKind.ExcludeKeyword, "gore"),
            (FilterKind.Language, "en"),
            (FilterKind.MinCrowdRating, "6.0"),
            (FilterKind.MinYear, "2000"),
            (FilterKind.MinPredictedRating, "6.0"));
        var artifact = await SeedArtifactAsync(account);

        // Footprint: seen (watched; rated; watched AND failing a rule), saved, not interested.
        await WatchAsync(account, await SeedTitleAsync(topic));
        await RateAsync(account, await SeedTitleAsync(topic));
        await WatchAsync(account, await SeedTitleAsync(topic, t => t.Genres = ["comedy"]));
        await ReactAsync(account, await SeedTitleAsync(topic), ReactionKind.SaveForLater);
        await ReactAsync(account, await SeedTitleAsync(topic, t => t.Year = 1990), ReactionKind.NotInterested);

        // One rule each (knockouts), two of one kind, and two at once (no single drop unlocks it).
        await SeedTitleAsync(topic, t => t.Genres = ["comedy", "action"]);
        await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);
        await SeedTitleAsync(topic, t => t.Genres = ["horror"]);
        await SeedTitleAsync(topic, t => t.Keywords = [topic, "gore fest"]);
        await SeedTitleAsync(topic, t => t.Language = "fr");
        await SeedTitleAsync(topic, t => t.Language = null);
        await SeedTitleAsync(topic, t => t.TmdbVoteAverage = 5.2m);
        await SeedTitleAsync(topic, t => t.Year = 1985);
        await SeedTitleAsync(topic, t => (t.Genres, t.Language) = (["comedy"], "fr"));

        // Eligible: clears the bar, under it, on it at badge precision (5.95 → 6.0), scored only by an
        // older artifact, never scored; and a Trakt-led title whose vote count is Trakt's.
        var clears = await SeedTitleAsync(topic);
        var under = await SeedTitleAsync(topic);
        var roundsUp = await SeedTitleAsync(topic);
        var stale = await SeedTitleAsync(topic);
        await SeedTitleAsync(topic);
        var traktLed = await SeedTitleAsync(topic, t => (t.TraktRating, t.TraktVotes, t.TmdbVoteCount) = (7.0m, 80, 10));
        await ScoreAsync(account, clears, artifact, 7.4m);
        await ScoreAsync(account, under, artifact, 5.94m);
        await ScoreAsync(account, roundsUp, artifact, 5.95m);
        await ScoreAsync(account, stale, Guid.NewGuid(), 8.0m);
        await ScoreAsync(account, traktLed, artifact, 6.5m);

        // Under the vote floor on each source: counted once, as low-vote matches.
        await SeedTitleAsync(topic, t => t.TmdbVoteCount = 12);
        await SeedTitleAsync(topic, t =>
        {
            t.TraktRating = 7.0m;
            t.TraktVotes = 30;
            t.TmdbVoteAverage = null;
            t.TmdbVoteCount = 0;
        });

        // Off-topic: never part of either count.
        await SeedTitleAsync($"goldensupply{Guid.NewGuid():N}");

        var (report, truth) = await BothAsync(account, topic);

        Assert.Equal(Shape(report), truth);
        // Non-vacuous: the fixture's own arithmetic, so a statement that silently stopped counting fails.
        Assert.Equal(
            "matching=20|lowVoteMatches=2|alreadySeen=3|savedOrDismissed=2|excludedByConditions=9|eligible=6"
            + "|unscored=2|belowFloor=1|clearingFloor=3"
            + "|ExcludeGenre/comedy=2|ExcludeGenre/horror=1|ExcludeKeyword/gore=1|Language/en=2|MinCrowdRating/6.0=1|MinYear/2000=1",
            truth);
    }

    [Fact]
    public async Task Supply_truth_equals_the_reporter_under_a_maturity_ceiling_and_a_single_genre_allow_list()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"goldensupply{Guid.NewGuid():N}";
        var account = await SeedAccountAsync("PG-13", (FilterKind.IncludeGenre, "thriller"), (FilterKind.Language, "*"));

        await SeedTitleAsync(topic, t => (t.Genres, t.Certification) = (["thriller"], "PG-13"));
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification, t.Language) = (["thriller"], "PG-13", "ko"));
        // Outside the allow-list only: unlocked by dropping it.
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification) = (["drama"], "PG"));
        // Outside the allow-list AND over the ceiling: dropping the allow-list unlocks nothing.
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification) = (["drama"], "R"));
        // Over the ceiling only (and unrated, which a PG-13 ceiling does not admit): held out, no relaxation.
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification) = (["thriller"], "R"));
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification) = (["thriller"], null));
        // A show, so the media scope has something to cut.
        await SeedTitleAsync(topic, t => (t.Genres, t.Certification, t.MediaType) = (["drama"], "TV-PG", MediaType.Show));

        var (report, truth) = await BothAsync(account, topic);
        Assert.Equal(Shape(report), truth);
        Assert.Equal(
            "matching=7|lowVoteMatches=0|alreadySeen=0|savedOrDismissed=0|excludedByConditions=5|eligible=2"
            + "|unscored=2|belowFloor=0|clearingFloor=0|IncludeGenre/thriller=2",
            truth);

        var (movieReport, movieTruth) = await BothAsync(account, topic, MediaScope.Movies);
        Assert.Equal(Shape(movieReport), movieTruth);
        Assert.StartsWith("matching=6|", movieTruth, StringComparison.Ordinal);

        // IREV2-SYS-03: the eval reads these counts back out of the REPLY's words
        // (scripts/eval/askreel-score.mjs replySupply). If the copy is reworded, this fails here and points
        // at the parser, instead of the eval silently finding no numbers to hold to the truth.
        var reply = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(report, topic, MediaScope.Any, [], 0, 0m));
        Assert.Contains(
            $"Of the {report.MatchingInCatalogue} {topic} titles in the catalogue with at least {report.MinVotes} votes, your conditions hold out {report.ExcludedByConditions}.",
            reply,
            StringComparison.Ordinal);
        Assert.Contains($"{report.Unscored} have no score from your model yet.", reply, StringComparison.Ordinal);
        var knockout = Assert.Single(report.Knockouts);
        Assert.Equal("only these genres: thriller", knockout.Label);
        Assert.Contains($"Turning off “{knockout.Label}” would unlock {knockout.WouldUnlock}.", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supply_truth_equals_the_reporter_when_a_stand_in_keyword_or_alias_is_the_knockout()
    {
        if (!fixture.Available)
        {
            return;
        }

        // IREV3-SYS-02's live cases in miniature: an animation film that also carries the "anime" keyword
        // fails TWO exclusions, so dropping animation alone unlocks nothing (the truth used to count it as an
        // animation knockout); a romantic show and a period-drama show are held out by their stand-ins.
        var topic = $"goldensupply{Guid.NewGuid():N}";
        var account = await SeedAccountAsync(null,
            (FilterKind.ExcludeGenre, "animation"),
            (FilterKind.ExcludeGenre, "anime"),
            (FilterKind.ExcludeGenre, "romance"),
            (FilterKind.ExcludeGenre, "history"),
            (FilterKind.ExcludeGenre, "thriller"));

        await SeedTitleAsync(topic, t => (t.Genres, t.Keywords) = (["animation"], [topic, "anime"]));
        await SeedTitleAsync(topic, t => t.Genres = ["animation"]);
        await SeedTitleAsync(topic, t => (t.MediaType, t.Keywords) = (MediaType.Show, [topic, "romantic"]));
        await SeedTitleAsync(topic, t => (t.MediaType, t.Keywords) = (MediaType.Show, [topic, "period drama"]));
        await SeedTitleAsync(topic, t => (t.MediaType, t.Genres) = (MediaType.Show, ["suspense"]));
        // Eligible: the same labels on a film (which has romance and history ids), and a show with "bromance".
        await SeedTitleAsync(topic, t => t.Keywords = [topic, "romantic", "period drama"]);
        await SeedTitleAsync(topic, t => (t.MediaType, t.Keywords) = (MediaType.Show, [topic, "bromance"]));

        var (report, truth) = await BothAsync(account, topic);

        Assert.Equal(Shape(report), truth);
        Assert.Equal(
            "matching=7|lowVoteMatches=0|alreadySeen=0|savedOrDismissed=0|excludedByConditions=5|eligible=2"
            + "|unscored=2|belowFloor=0|clearingFloor=0"
            + "|ExcludeGenre/animation=1|ExcludeGenre/history=1|ExcludeGenre/romance=1|ExcludeGenre/thriller=1",
            truth);
    }

    /// <summary>The reporter's counts and the eval's SQL truth for one topic, each as a comparable line.</summary>
    private async Task<(SupplyReport Report, string Truth)> BothAsync(Guid accountId, string topic, MediaScope media = MediaScope.Any)
    {
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], [])], Media = media };
        await using var db = fixture.CreateContext(new AccountContext());
        var report = await new SupplyReporter(db, new EligibilityQueryBuilder(db))
            .ReportAsync(accountId, hard, CrowdVotes.MinRelaxVotes, CancellationToken.None);

        var scope = media switch
        {
            MediaScope.Movies => "Movie",
            MediaScope.Shows => "Show",
            _ => "Any",
        };
        var sql = string.Join(
            "\n",
            $"CREATE TEMP VIEW eval_concept AS SELECT t.\"Id\" FROM \"Titles\" t WHERE '{topic}' = ANY (t.\"Keywords\");",
            $"CREATE TEMP VIEW eval_eligible AS {Bind(Read("truth", "_eligible.sql"), accountId)};",
            $"CREATE TEMP VIEW eval_rules AS {Bind(Read("supply", "rules.sql"), accountId)};",
            Bind(Read("supply", "supply.sql"), accountId)
                .Replace(":'media'", $"'{scope}'", StringComparison.Ordinal)
                .Replace(":minvotes", CrowdVotes.MinRelaxVotes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        var buckets = new List<string>();
        var knockouts = new List<(string Kind, string Value, int Count)>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount != 4)
                {
                    continue;
                }

                var (bucket, kind, value, count) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
                if (bucket == "knockout")
                {
                    knockouts.Add((kind, value, count));
                }
                else
                {
                    buckets.Add($"{bucket}={count}");
                }
            }
        }
        while (await reader.NextResultAsync());

        var truth = string.Join("|", buckets.Concat(knockouts
            .OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Value, StringComparer.Ordinal)
            .Select(k => $"{k.Kind}/{k.Value}={k.Count}")));
        return (report, truth);
    }

    /// <summary>The reporter's counts in the SQL truth's order and vocabulary.</summary>
    private static string Shape(SupplyReport r) =>
        string.Join("|", new[]
        {
            $"matching={r.MatchingInCatalogue}", $"lowVoteMatches={r.LowVoteMatches}", $"alreadySeen={r.AlreadySeen}",
            $"savedOrDismissed={r.SavedOrDismissed}", $"excludedByConditions={r.ExcludedByConditions}", $"eligible={r.Eligible}",
            $"unscored={r.Unscored}", $"belowFloor={r.BelowFloor}", $"clearingFloor={r.ClearingFloor}",
        }.Concat(r.Knockouts
            .OrderBy(k => k.Kind.ToString(), StringComparer.Ordinal).ThenBy(k => k.Value, StringComparer.Ordinal)
            .Select(k => $"{k.Kind}/{k.Value}={k.WouldUnlock}")));

    private static string Read(string folder, string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "eval", "askreel", folder, file));

    /// <summary>The eval's query files take the account as the psql variable <c>:'account'</c>; bind it as a literal.</summary>
    private static string Bind(string sql, Guid accountId)
    {
        var lines = sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        return string.Join('\n', lines).Replace(":'account'", $"'{accountId}'", StringComparison.Ordinal).TrimEnd().TrimEnd(';');
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }

    private async Task<Guid> SeedAccountAsync(string? maturityCeiling, params (FilterKind Kind, string Value)[] filters)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"golden-supply-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        account.Settings.MaturityCeiling = maturityCeiling;
        db.Add(account);
        var at = DateTime.UtcNow;
        foreach (var (kind, value) in filters)
        {
            at = at.AddMilliseconds(1);
            db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = account.Id, Kind = kind, Value = value, CreatedAt = at });
        }

        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// A title that passes every rule the tests configure unless <paramref name="shape"/> says otherwise:
    /// an English 2010 drama, crowd 7.5 on 1,000 TMDB votes, tagged with the topic.
    /// </summary>
    private async Task<Guid> SeedTitleAsync(string topic, Action<Title>? shape = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"golden-supply {Guid.NewGuid():N}",
            Language = "en",
            Year = 2010,
            RuntimeMinutes = 100,
            Genres = ["drama"],
            Keywords = [topic],
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 1_000,
            // Bottom of the popularity order, so these fixtures never displace another test's top-N.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape?.Invoke(title);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task<Guid> SeedArtifactAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var id = Guid.NewGuid();
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = id,
            AccountId = accountId,
            Version = 1,
            Status = ArtifactStatus.Active,
            // The schema version must be current or the global query filter hides the artifact.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task ScoreAsync(Guid accountId, Guid titleId, Guid artifactId, decimal predicted)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleScores.Add(new TitleScore
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ModelArtifactId = artifactId,
            PredictedRating = predicted,
            ScoredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task WatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task RateAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 7,
            RatedAt = DateTime.UtcNow,
            FirstRatedAt = DateTime.UtcNow,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task ReactAsync(Guid accountId, Guid titleId, ReactionKind kind)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
