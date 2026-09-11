using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The anchor provenance policy against real Postgres, reproducing the founder's live failure.
///
/// <para><b>What actually happened, from the two Rejected rows.</b> Both notes anchored on titles
/// taken straight out of the DECLINED line — "Frontera", "Lawless", "Amateur", "The Girl with All
/// the Gifts" — because the declined line was the only list of title names anywhere in the lens
/// prompt, and the system prompt told the model to take anchors "verbatim from the lists below".
/// The verifier accepted only titles the account had RATED, which by construction excludes every
/// declined title, so both notes were refused and the surface fell back. The rejection rate on the
/// on-demand path was one hundred percent and would have stayed there for ever.</para>
///
/// <para>Three fixes, one per defect, and a case each below: the library the anchor is checked
/// against is the account's whole opinion set rather than its ratings; the normalisation actually
/// runs, where before an in-SQL pre-filter made it unreachable; and an unresolvable anchor is
/// stripped rather than fatal, leaving only PROSE-level invention as grounds for refusal.</para>
/// </summary>
[Collection("postgres")]
public class LensAnchorPolicyTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private static LensNote Note(params string[] anchors) => new(
        "If you want a bleak procedural that stays close to its own logic, this is that film, and "
        + "it keeps the pressure on without ever raising its voice.",
        // Second person since prompt v4: the hook-voice guard refuses a third-person tagline, and
        // this fixture runs the full generation path in the end-to-end cases below.
        "You get a quiet, relentless piece of work.",
        "It commits to being slow before it commits to being anything else.",
        anchors.Select(a => new LensAnchor(a, "sits in the same register as this one")).ToList(),
        "tense",
        "tonight-alone");

    // ── (a) The library the anchors are checked against ─────────────────────────────────────

    [Fact]
    public async Task An_anchor_the_account_declined_rather_than_rated_now_resolves()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        // The exact four anchors in the two live Rejected rows. Every one is a title the account
        // has an opinion about, and every one was called an invention.
        var verdict = await new LensVerifier(db).VerifyAsync(
            accountId, "The Subject", Note("Frontera", "Lawless", "The Girl with All the Gifts"), [], default);

        Assert.Null(verdict.Reason);
        Assert.Empty(verdict.StrippedAnchors);
        Assert.Equal(3, verdict.Note.Anchors.Count);
    }

    [Fact]
    public async Task An_anchor_the_account_watched_without_rating_resolves()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", Note("The Amateur"), [], default);

        Assert.Null(verdict.Reason);
        Assert.Empty(verdict.StrippedAnchors);
    }

    [Fact]
    public async Task Another_accounts_library_is_not_this_accounts_library()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedLibraryAsync();
        var theirs = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(theirs));

        // Widening the library from ratings to ratings ∪ watched ∪ reacted widens what is
        // ACCEPTED. It must not widen it across the tenancy boundary — the second account here
        // holds its own rows and nothing of the first account's.
        var verdict = await new LensVerifier(db).VerifyAsync(theirs, "The Subject", Note("Frontera"), [], default);

        Assert.NotEqual(mine, theirs);
        Assert.Null(verdict.Reason);
    }

    // ── (a, continued) The normalisation that used to be unreachable ─────────────────────────

    [Fact]
    public async Task A_leading_article_a_year_and_a_typo_are_not_inventions()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var verifier = new LensVerifier(db);

        // "Amateur" for a row called "The Amateur" — the live shape, and the one the old code
        // could never rescue: it pre-filtered library rows to exact lowercase matches in SQL and
        // only normalised the survivors, so the normaliser only ever saw names that had already
        // matched. Every case here failed before.
        foreach (var written in new[] { "Amateur", "Frontera (2014)", "lawless", "Frontiera" })
        {
            var verdict = await verifier.VerifyAsync(accountId, "The Subject", Note(written), [], default);
            Assert.True(
                verdict.Reason is null && verdict.StrippedAnchors.Count == 0,
                $"'{written}' names a title in this account's library and must resolve.");
        }
    }

    // ── (b) Strip the anchor, keep the note; only prose invention refuses ────────────────────

    [Fact]
    public async Task An_invented_anchor_is_stripped_and_the_note_survives()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var verdict = await new LensVerifier(db).VerifyAsync(
            accountId, "The Subject", Note("Frontera", "Emily in Paris"), [], default);

        // The prose was never wrong. Refusing four sentences over one droppable comparison is what
        // took the on-demand path to a hundred percent rejection.
        Assert.Null(verdict.Reason);
        Assert.Equal(["Emily in Paris"], verdict.StrippedAnchors);
        Assert.Equal(["Frontera"], verdict.Note.Anchors.Select(a => a.Title));
    }

    [Fact]
    public async Task A_note_whose_every_anchor_is_invented_still_survives_as_prose()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var verdict = await new LensVerifier(db).VerifyAsync(
            accountId, "The Subject", Note("Emily in Paris", "Some Film Nobody Rated"), [], default);

        Assert.Null(verdict.Reason);
        Assert.Empty(verdict.Note.Anchors);
        Assert.Equal(2, verdict.StrippedAnchors.Count);
    }

    [Fact]
    public async Task A_title_the_prose_itself_invents_is_still_fatal()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        // The narrowing is to the ANCHOR field, not to grounding as a whole. A title asserted in
        // the prose cannot be dropped without rewriting the sentence, so it still sinks the note.
        var note = Note() with
        {
            Lens = "It moves the way Blade Runner did, and it keeps the pressure on without ever "
                + "raising its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", note, [], default);

        Assert.Equal("invented-anchor:Blade Runner", verdict.Reason);
    }

    [Fact]
    public async Task The_subject_title_named_in_its_own_prose_is_not_an_invented_anchor()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        // The live failure: "Killer Elite gives you…" refused as invented-anchor:Killer Elite. The
        // subject is in the catalogue and, by construction, not in the library — it is the film the
        // note is recommending. Naming it is not a fabricated connection to the viewer's history.
        var note = Note() with
        {
            Lens = "Blade Runner gives you a slow, rain-soaked hunt that keeps the pressure on "
                + "without ever raising its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "Blade Runner", note, [], default);

        Assert.Null(verdict.Reason);
        Assert.Empty(verdict.StrippedAnchors);
    }

    [Fact]
    public async Task A_title_that_opens_a_sentence_is_a_known_blind_spot_of_the_prose_check()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        // Documented, not accidental. The phrase extractor takes two or three consecutive
        // capitalised words, so a capitalised sentence-opener FUSES onto the title after it and
        // the resulting phrase matches no catalogue row. Lowercasing openers would fix it here and
        // would simultaneously blind the PERSON check to every name that opens a sentence, which
        // is the more valuable of the two guards. So this direction of error is accepted: it lets
        // an invention through, and the alternative refuses truths.
        var note = Note() with
        {
            Lens = "Where Blade Runner drifted, this one keeps the pressure on without ever "
                + "raising its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", note, [], default);

        Assert.Null(verdict.Reason);
    }

    [Fact]
    public async Task A_capitalised_phrase_the_catalogue_has_never_heard_of_is_not_a_title()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        // Same reasoning the person check is built on: an unknown capitalised phrase is far more
        // likely to be a character or a place out of the overview than a fabricated film. Only
        // phrases the CATALOGUE knows as titles are convictable.
        var note = Note() with
        {
            Lens = "A cold night in Little Whinging, and it keeps the pressure on without ever "
                + "raising its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", note, [], default);

        Assert.Null(verdict.Reason);
    }

    [Fact]
    public async Task A_title_named_in_the_prose_that_the_account_owns_is_fine()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var note = Note("Frontera") with
        {
            Lens = "Where Lawless swaggered, this one keeps the pressure on without ever raising "
                + "its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", note, [], default);

        Assert.Null(verdict.Reason);
    }

    [Fact]
    public async Task An_invented_person_still_outranks_everything()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var note = Note("Frontera") with
        {
            Lens = "This is Denis Villeneuve territory, and it keeps the pressure on without ever "
                + "raising its voice.",
        };
        var verdict = await new LensVerifier(db).VerifyAsync(accountId, "The Subject", note, [], default);

        Assert.Equal("invented-person:Denis Villeneuve", verdict.Reason);
    }

    // ── (c) The prompt names its own anchors ─────────────────────────────────────────────────

    [Fact]
    public async Task The_persona_offers_loved_disliked_and_declined_titles_by_name()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dislikes = new DislikeSignal(db, cache);
        var personalisation = new PersonalisationContext(db, dislikes);

        var candidates = await personalisation.AnchorCandidatesAsync(
            accountId, await dislikes.GetProfileAsync(accountId, default), default);

        var byName = candidates.ToDictionary(c => c.Name, StringComparer.Ordinal);

        // A loved title, with the rating that earns it its stance.
        Assert.Equal(AnchorStance.Loved, byName["Sicario"].Stance);
        Assert.Equal((short)10, byName["Sicario"].Rating);
        Assert.Equal(2015, byName["Sicario"].Year);

        // A disliked title — an anchor may be something you did not like.
        Assert.Equal(AnchorStance.Disliked, byName["Argylle"].Stance);

        // And the declined titles, which had been reaching the prompt through the declined line
        // and being refused by the guard. They are candidates on purpose now.
        Assert.Equal(AnchorStance.Declined, byName["Frontera"].Stance);
        Assert.Null(byName["Frontera"].Rating);
    }

    [Fact]
    public async Task Every_anchor_the_prompt_offers_is_one_the_verifier_accepts()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dislikes = new DislikeSignal(db, cache);
        var personalisation = new PersonalisationContext(db, dislikes);
        var verifier = new LensVerifier(db);

        var candidates = await personalisation.AnchorCandidatesAsync(
            accountId, await dislikes.GetProfileAsync(accountId, default), default);

        // THE invariant. A model that does exactly what the prompt tells it to do must never be
        // refused — that disagreement between the prompt and the guard is the entire defect, and a
        // test that only checked the guard would have passed throughout the outage.
        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            var verdict = await verifier.VerifyAsync(accountId, "The Subject", Note(candidate.Name), [], default);
            Assert.True(
                verdict.Reason is null && verdict.StrippedAnchors.Count == 0,
                $"The prompt offers '{candidate.Name}' as an anchor and the guard refused it.");
        }
    }

    // ── (b, d) End to end: what the row on disk actually says ────────────────────────────────

    [Fact]
    public async Task A_generated_note_is_stored_WITHOUT_the_anchor_that_was_stripped()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        var titleId = await SeedCandidateAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var outcome = await GenerateAsync(
            db, accountId, titleId, Note("Frontera", "Emily in Paris"));

        Assert.Equal(1, outcome.Written);
        Assert.Equal(0, outcome.Rejected);

        var row = await db.TitleLensCopies.SingleAsync(c => c.TitleId == titleId);
        Assert.Equal(LensStatus.Done, row.Status);
        Assert.Null(row.RejectionReason);

        // The stored JSON is the TRIMMED note, not the draft. A stripped anchor that survives into
        // LensJson is a dropped anchor that renders again the next time anything reads the row.
        var stored = JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions);
        Assert.Equal(["Frontera"], stored!.Anchors.Select(a => a.Title));
    }

    [Fact]
    public async Task A_refused_note_stores_the_reason_WITH_the_token_that_caused_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedLibraryAsync();
        var titleId = await SeedCandidateAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        var invented = Note() with
        {
            Lens = "It moves the way Blade Runner did, and it keeps the pressure on without ever "
                + "raising its voice.",
        };
        var outcome = await GenerateAsync(db, accountId, titleId, invented);

        Assert.Equal(0, outcome.Written);
        Assert.Equal(1, outcome.Rejected);

        var row = await db.TitleLensCopies.SingleAsync(c => c.TitleId == titleId);
        Assert.Equal(LensStatus.Rejected, row.Status);
        // The token is what makes a refusal readable as evidence. Without it the audit can only
        // report a count, and a count of two told nobody that both notes named declined titles.
        Assert.Equal("invented-anchor:Blade Runner", row.RejectionReason);
        Assert.True(row.RejectionReason!.Length <= 128);
    }

    /// <summary>
    /// Runs the real generation path with a writer that returns exactly <paramref name="note"/>.
    /// The deterministic stub only ever anchors on candidates the persona offered, which is
    /// correct behaviour and therefore useless for testing what happens when a model does not.
    /// </summary>
    private static async Task<LensService.GenerateOutcome> GenerateAsync(
        ReelDbContext db, Guid accountId, Guid titleId, LensNote note)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build();
        var dislikes = new DislikeSignal(db, cache);
        var service = new LensService(
            db,
            new FixedNoteWriter(note),
            new LensInputBuilder(db, new RatingCalibrationService(db, cache)),
            new LensVerifier(db),
            new LensBudget(db, configuration),
            new PersonalisationContext(db, dislikes),
            NullLogger<LensService>.Instance);

        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, Now, default);
        return await service.GenerateForJobAsync(accountId, taste, "abc123abc123", [titleId], default);
    }

    /// <summary>A writer that returns one predetermined note. Never reaches a provider.</summary>
    private sealed class FixedNoteWriter(LensNote note) : ILensWriter
    {
        public bool IsAvailable => true;

        public string ModelId => "stub/fixed-note";

        public int LensPromptVersion => 2;

        public int NarrativePromptVersion => 2;

        public Task<NarrativeDraft?> WriteNarrativeAsync(TasteFacts facts, CancellationToken ct = default) =>
            Task.FromResult<NarrativeDraft?>(null);

        public Task<IReadOnlyList<LensDraft?>> WriteLensAsync(
            IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LensDraft?>>(
                titles.Select(_ => (LensDraft?)new LensDraft(note, ModelId, 10, 10)).ToArray());

        public Task<LensAuditScore?> JudgeAsync(
            LensTitleInput title, LensPersona persona, LensNote note_, CancellationToken ct = default) =>
            Task.FromResult<LensAuditScore?>(null);
    }

    /// <summary>A title to generate a note ABOUT — separate from the library it anchors against.</summary>
    private async Task<Guid> SeedCandidateAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktSlug = $"anchor-candidate-{Guid.NewGuid():N}",
            TmdbId = Random.Shared.Next(1, int.MaxValue),
            Name = "The Candidate Title",
            Year = 2019,
            Genres = ["crime", "thriller"],
            Overview = "A task force escalates the war on the cartels.",
            CreatedAt = Now,
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// An account shaped like the founder's at the moment of the failure: a few rated titles, one
    /// watched-but-never-rated title whose name carries an article, and the declined titles the
    /// model actually anchored on.
    /// </summary>
    private async Task<Guid> SeedLibraryAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var accountId = Guid.NewGuid();
        db.Accounts.Add(new Account
        {
            Id = accountId,
            TraktUserSlug = $"anchor-{accountId:N}",
            TraktUsername = "anchor",
            DisplayName = "Anchor",
            CreatedAt = Now,
            LastSeenAt = Now,
        });

        async Task<Guid> TitleAsync(string name, int year)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"anchor-{Guid.NewGuid():N}",
                TmdbId = Random.Shared.Next(1, int.MaxValue),
                Name = name,
                Year = year,
                Genres = ["crime", "thriller"],
                CreatedAt = Now,
            };
            db.Titles.Add(title);
            await Task.CompletedTask;
            return title.Id;
        }

        void Rate(Guid titleId, short rating) => db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = Now.AddDays(-3),
        });

        void Decline(Guid titleId, ReactionReason reason) => db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = ReactionKind.NotInterested,
            Reason = reason,
            CreatedAt = Now.AddDays(-1),
        });

        Rate(await TitleAsync("Sicario", 2015), 10);
        Rate(await TitleAsync("Heat", 1995), 9);
        Rate(await TitleAsync("Argylle", 2024), 3);

        // Watched, never rated. The model wrote "Amateur"; the row is "The Amateur".
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = await TitleAsync("The Amateur", 2025),
            Plays = 1,
            IsFullyWatched = true,
            FirstSyncedAt = Now,
            UpdatedAt = Now,
        });

        // The three declined titles the live notes anchored on, with the reasons the live rows had.
        Decline(await TitleAsync("Frontera", 2014), ReactionReason.NotForMe);
        Decline(await TitleAsync("Lawless", 2012), ReactionReason.SeenEnough);
        Decline(await TitleAsync("The Girl with All the Gifts", 2016), ReactionReason.Genre);

        // In the catalogue and in NOBODY's library — what an actual invention looks like.
        await TitleAsync("Emily in Paris", 2020);
        await TitleAsync("Blade Runner", 1982);

        // A real film person, credited on nothing here. The person guard convicts only names the
        // CATALOGUE knows, so without this row there is nothing to convict.
        db.Persons.Add(new Person
        {
            Id = Guid.NewGuid(),
            Name = "Denis Villeneuve",
            TmdbId = Random.Shared.Next(1, int.MaxValue),
        });

        await db.SaveChangesAsync();
        return accountId;
    }
}
