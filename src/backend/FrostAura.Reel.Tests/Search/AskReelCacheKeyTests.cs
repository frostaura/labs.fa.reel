using System.Reflection;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// What an Ask Reel cache key has to be a function of.
///
/// <para>A cache key that is missing a field does not fail — it answers, confidently, with somebody
/// else's results, for the whole six-hour TTL. Four fields were missing when this was written: the
/// two exclusion lists (so "a tense thriller, no horror" was served the cached, unexcluded answer to
/// "a tense thriller"), the region (so two accounts asking the same thing at the same moment shared
/// one fetch and one got the other's catalogue), and the prompt generation plus model id on the
/// caches that hold a model's output.</para>
///
/// <para>The property table below is walked by REFLECTION rather than written out, so a field added
/// to <see cref="SearchIntent"/> tomorrow fails this suite until somebody decides, in writing,
/// whether it belongs in the key. That decision is the thing being pinned; the assertions are just
/// how it is held.</para>
/// </summary>
public class AskReelCacheKeyTests
{
    private static readonly SearchIntent Base = new(
        Genres: ["thriller"],
        Keywords: ["tense"],
        MediaTypes: [MediaType.Movie],
        FreeText: "a tense thriller",
        MinYear: 1990,
        MaxYear: 2010,
        People: [new PersonQuery("Christopher Nolan", PersonRole.Director)],
        ExcludeGenres: ["horror"],
        ExcludeKeywords: ["ghosts"],
        ReferenceTitles: ["Blade Runner"],
        MaxRuntimeMinutes: 120);

    /// <summary>
    /// One mutation per intent field that MUST change the discovery key, because each one changes
    /// the query TMDB is asked.
    /// </summary>
    private static readonly Dictionary<string, Func<SearchIntent, SearchIntent>> Mutations = new()
    {
        [nameof(SearchIntent.Genres)] = i => i with { Genres = ["drama"] },
        [nameof(SearchIntent.Keywords)] = i => i with { Keywords = ["quiet"] },
        [nameof(SearchIntent.MediaTypes)] = i => i with { MediaTypes = [MediaType.Show] },
        [nameof(SearchIntent.FreeText)] = i => i with { FreeText = "a quiet drama" },
        [nameof(SearchIntent.MinYear)] = i => i with { MinYear = 1991 },
        [nameof(SearchIntent.MaxYear)] = i => i with { MaxYear = 2011 },
        [nameof(SearchIntent.People)] = i => i with { People = [new PersonQuery("Denis Villeneuve", PersonRole.Director)] },
        [nameof(SearchIntent.ExcludeGenres)] = i => i with { ExcludeGenres = ["comedy"] },
        [nameof(SearchIntent.ExcludeKeywords)] = i => i with { ExcludeKeywords = ["clowns"] },
        [nameof(SearchIntent.MaxRuntimeMinutes)] = i => i with { MaxRuntimeMinutes = 90 },
    };

    /// <summary>
    /// The one field deliberately left OUT of the discovery key, with its reason.
    ///
    /// <para><see cref="SearchIntent.ReferenceTitles"/> never reaches TMDB. A named comparison is
    /// resolved to a catalogue title separately and its embedding is blended into the RANKING
    /// vector; discovery is asked the identical question either way. Two turns differing only in
    /// what they name as a comparison therefore genuinely do share a pull, and keying on it would
    /// buy a duplicate TMDB fetch for no change in what comes back.</para>
    /// </summary>
    private static readonly Dictionary<string, Func<SearchIntent, SearchIntent>> KeyExempt = new()
    {
        [nameof(SearchIntent.ReferenceTitles)] = i => i with { ReferenceTitles = ["Alien"] },
    };

    [Fact]
    public void Every_intent_property_either_changes_the_key_or_is_explicitly_exempt()
    {
        var unaccounted = typeof(SearchIntent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !Mutations.ContainsKey(name) && !KeyExempt.ContainsKey(name))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"SearchIntent has field(s) no cache-key decision has been taken about: {string.Join(", ", unaccounted)}. "
            + "Add a mutation to Mutations if the field changes what TMDB is asked, or add it to KeyExempt "
            + "WITH the reason it cannot.");
    }

    [Fact]
    public void Two_intents_differing_in_one_property_produce_different_keys()
    {
        var baseline = LiveSearchExpansionService.IntentKey(Base, "US");
        foreach (var (name, mutate) in Mutations)
        {
            Assert.True(
                LiveSearchExpansionService.IntentKey(mutate(Base), "US") != baseline,
                $"changing {name} left the discovery cache key unchanged — the next turn will be served "
                + "the previous turn's TMDB results.");
        }
    }

    [Fact]
    public void A_named_reference_title_is_deliberately_not_part_of_the_discovery_key()
    {
        foreach (var (_, mutate) in KeyExempt)
        {
            Assert.Equal(
                LiveSearchExpansionService.IntentKey(Base, "US"),
                LiveSearchExpansionService.IntentKey(mutate(Base), "US"));
        }
    }

    /// <summary>
    /// Region rides the INTENT key, not merely the outer cache key, because the single-flight key
    /// that shares one TMDB fetch between concurrent identical searches is derived from the intent
    /// key alone. Split them and two regions share a fetch.
    /// </summary>
    [Fact]
    public void Region_rides_the_intent_key_so_the_single_flight_cannot_cross_regions()
    {
        Assert.NotEqual(
            LiveSearchExpansionService.IntentKey(Base, "US"),
            LiveSearchExpansionService.IntentKey(Base, "ZA"));
        Assert.NotEqual(
            LiveSearchExpansionService.IntentKey(Base, null),
            LiveSearchExpansionService.IntentKey(Base, "US"));
    }

    [Fact]
    public void The_discovered_id_key_carries_the_prompt_generation_and_the_model()
    {
        var key = LiveSearchExpansionService.CacheKey(
            LiveSearchExpansionService.IntentKey(Base, "US"), "openai/gpt-5.4-mini");

        Assert.Contains(AskReelPromptVersion.Current, key, StringComparison.Ordinal);
        Assert.Contains("openai/gpt-5.4-mini", key, StringComparison.Ordinal);
        Assert.NotEqual(key, LiveSearchExpansionService.CacheKey(
            LiveSearchExpansionService.IntentKey(Base, "US"), "some/other-model"));
    }

    [Fact]
    public void The_rerank_fit_key_carries_the_salt_the_prompt_generation_and_the_batch()
    {
        var accountId = Guid.NewGuid();
        var titleId = Guid.NewGuid();
        var other = Guid.NewGuid();
        const string Salt = "rr2:openai/gpt-5.4-mini|none:p00000000";
        Guid[] batch = [titleId, other];
        var key = LiveSearchExpansionService.FitKey(Salt, accountId, titleId, "a tense thriller", batch);

        // WS-1 CR-4: the shared re-rank key shape, so a model, effort or prompt change misses.
        Assert.StartsWith("askfit:" + Salt + ":", key, StringComparison.Ordinal);
        Assert.Contains(AskReelPromptVersion.Current, key, StringComparison.Ordinal);
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey("rr2:openai/gpt-5.4-mini|low:p00000000", accountId, titleId, "a tense thriller", batch));
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey("rr2:some/other-model|none:p00000000", accountId, titleId, "a tense thriller", batch));
        // Still account- and title-scoped: a verdict is one person's, about one title…
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(Salt, Guid.NewGuid(), titleId, "a tense thriller", batch));
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(Salt, accountId, Guid.NewGuid(), "a tense thriller", batch));
        // …about one request, among one set of companions (the prompt spreads fits across the batch),
        // in any order.
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(Salt, accountId, titleId, "a quiet drama", batch));
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(Salt, accountId, titleId, "a tense thriller", [titleId]));
        Assert.Equal(key, LiveSearchExpansionService.FitKey(Salt, accountId, titleId, "a tense thriller", [other, titleId]));
    }

    // ── The interpret key ───────────────────────────────────────────────────────────────────
    //
    // The interpretation is a function of the question AND the conversation it was asked in: the
    // prompt is handed the transcript and the no-repeat set alongside the sentence. The first
    // version of this key carried only the sentence, and the cost was not a lower hit rate — it was
    // a confident wrong answer for the whole TTL. Thread A had said "only shows", so "thrillers, no
    // horror" resolved to shows there; + New, the identical sentence, and the fresh thread was
    // served thread A's interpretation, reply and all-TV cards.

    /// <summary>A transcript, and the same sentence asked at the end of it.</summary>
    private static readonly ChatTurn[] ShowsThread =
    [
        new("user", "only shows, please"),
        new("assistant", "Shows it is."),
    ];

    private static readonly Guid Shown603 = Guid.Parse("00000000-0000-0000-0000-000000000603");
    private static readonly Guid Shown604 = Guid.Parse("00000000-0000-0000-0000-000000000604");
    private static readonly Guid Shown605 = Guid.Parse("00000000-0000-0000-0000-000000000605");

    private static string Interpret(
        Guid accountId, string query, IReadOnlyList<ChatTurn> history, IReadOnlyList<Guid>? shown = null,
        IReadOnlyList<PresentedCard>? presented = null) =>
        LiveSearchExpansionService.InterpretKey(
            accountId, query, standingSummary: null, mediaFilter: [], "openai/gpt-5.4-mini", history,
            presented ?? [], shown ?? []);

    [Fact]
    public void The_same_question_in_two_conversations_produces_two_interpret_keys()
    {
        var accountId = Guid.NewGuid();

        Assert.NotEqual(
            Interpret(accountId, "thrillers, no horror", ShowsThread),
            Interpret(accountId, "thrillers, no horror", []));
    }

    /// <summary>
    /// The half that must NOT regress. The cache exists because a byte-identical repeat used to
    /// re-buy the interpret call and re-pull TMDB behind a drifted intent — ~21 s against ~2.5 s —
    /// so the same question with the same history behind it still has to be one entry. A fix that
    /// keyed on the conversation id, or on a clock, would pass the test above and lose this.
    /// </summary>
    [Fact]
    public void The_same_question_with_the_same_history_is_one_interpret_key()
    {
        var accountId = Guid.NewGuid();
        ChatTurn[] copy = [new("user", "only shows, please"), new("assistant", "Shows it is.")];

        Assert.Equal(
            Interpret(accountId, "thrillers, no horror", ShowsThread),
            Interpret(accountId, "thrillers, no horror", copy));
        Assert.Equal(
            Interpret(accountId, "thrillers, no horror", []),
            Interpret(accountId, "thrillers, no horror", []));
    }

    /// <summary>
    /// Order is part of the context. The model reads a transcript as a sequence, so a key that
    /// sorted or set-ified it would serve "first they asked for shows, then for thrillers" the
    /// answer to the reverse conversation.
    /// </summary>
    [Fact]
    public void Reordering_the_history_changes_the_interpret_key()
    {
        var accountId = Guid.NewGuid();
        ChatTurn[] reversed = [ShowsThread[1], ShowsThread[0]];

        Assert.NotEqual(
            Interpret(accountId, "thrillers, no horror", ShowsThread),
            Interpret(accountId, "thrillers, no horror", reversed));
    }

    /// <summary>
    /// The role is part of a turn's identity — the same words are a request from the user and a
    /// promise from the assistant, and the prompt labels them as such.
    /// </summary>
    [Fact]
    public void The_speaker_of_a_turn_changes_the_interpret_key()
    {
        var accountId = Guid.NewGuid();

        Assert.NotEqual(
            Interpret(accountId, "thrillers", [new ChatTurn("user", "only shows")]),
            Interpret(accountId, "thrillers", [new ChatTurn("assistant", "only shows")]));
    }

    /// <summary>
    /// The digest is length-prefixed per field, which is the whole difference between a digest and a
    /// concatenation — and this pair is the proof, not a formality. Concatenated raw, a two-turn
    /// transcript and a one-turn transcript whose text happens to swallow the next turn's role label
    /// flatten to the identical byte string: <c>user·xuser·user·y</c> and <c>user·xuserusery</c>.
    /// Two different conversations, one cache entry — the original defect, one layer down and far
    /// harder to see. A user can type "xuser" into a chat box; nothing stops them.
    /// </summary>
    [Fact]
    public void A_transcript_that_could_be_reparsed_as_another_still_gets_its_own_key()
    {
        var accountId = Guid.NewGuid();

        Assert.NotEqual(
            Interpret(accountId, "thrillers", [new ChatTurn("user", "xuser"), new ChatTurn("user", "y")]),
            Interpret(accountId, "thrillers", [new ChatTurn("user", "xuserusery")]));
    }

    /// <summary>
    /// "No history" is its own key rather than the digest of an empty transcript, so the commonest
    /// context in the product is legible in a cache dump — and is still distinct from every
    /// transcript that has anything in it, including an empty turn.
    /// </summary>
    [Fact]
    public void No_history_is_its_own_distinct_key()
    {
        var accountId = Guid.NewGuid();
        var fresh = Interpret(accountId, "thrillers, no horror", []);

        Assert.Contains("|h=none|", fresh, StringComparison.Ordinal);
        Assert.NotEqual(fresh, Interpret(accountId, "thrillers, no horror", [new ChatTurn("user", string.Empty)]));
    }

    /// <summary>
    /// The no-repeat set is a prompt input too — it arrives as "already shown this session (do not
    /// repeat; resolve references like 'the second one' against this list)", so it changes both what
    /// the model is told to avoid and what a pronoun resolves to. Same question, different shown
    /// set, different answer.
    /// </summary>
    [Fact]
    public void The_shown_set_rides_the_interpret_key()
    {
        var accountId = Guid.NewGuid();
        var nothingShown = Interpret(accountId, "something else like that", [], []);

        Assert.Contains("|s=none", nothingShown, StringComparison.Ordinal);
        Assert.NotEqual(nothingShown, Interpret(accountId, "something else like that", [], [Shown603]));
        Assert.NotEqual(
            Interpret(accountId, "something else like that", [], [Shown603]),
            Interpret(accountId, "something else like that", [], [Shown604]));
    }

    /// <summary>
    /// The presented handles are the list "the second one" resolves against, so they are a prompt
    /// input: the same question over a different second card is a different question (A-4).
    /// </summary>
    [Fact]
    public void The_presented_handles_ride_the_interpret_key()
    {
        var accountId = Guid.NewGuid();
        PresentedCard[] first = [new(2, 1, Shown603, "Ballerina", 2025, MediaType.Movie), new(2, 2, Shown604, "John Wick: Chapter 4", 2023, MediaType.Movie)];
        PresentedCard[] swapped = [new(2, 1, Shown604, "John Wick: Chapter 4", 2023, MediaType.Movie), new(2, 2, Shown603, "Ballerina", 2025, MediaType.Movie)];

        Assert.Contains("|p=none|", Interpret(accountId, "more like the second one", []), StringComparison.Ordinal);
        Assert.NotEqual(
            Interpret(accountId, "more like the second one", [], presented: first),
            Interpret(accountId, "more like the second one", [], presented: swapped));
        Assert.Equal(
            Interpret(accountId, "more like the second one", [], presented: first),
            Interpret(accountId, "more like the second one", [], presented: [.. first.Reverse()]));
    }

    /// <summary>
    /// The shown set is a SET: the endpoint reads it with a <c>Distinct()</c> and no <c>ORDER BY</c>,
    /// so Postgres owes it no row order at all. Keying on the raw sequence would split one context
    /// into arbitrarily many entries and silently cost the repeat hit this cache exists for.
    /// </summary>
    [Fact]
    public void The_shown_set_is_order_and_duplicate_insensitive()
    {
        var accountId = Guid.NewGuid();

        Assert.Equal(
            Interpret(accountId, "something else", [], [Shown603, Shown604, Shown605]),
            Interpret(accountId, "something else", [], [Shown605, Shown603, Shown604, Shown603]));
    }

    /// <summary>
    /// The tenancy guarantee the key has always carried, re-asserted through the new signature: a
    /// reply is written against one person's taste, and this is a process-wide cache.
    /// </summary>
    [Fact]
    public void Another_account_never_shares_an_interpret_key()
    {
        Assert.NotEqual(
            Interpret(Guid.NewGuid(), "thrillers, no horror", ShowsThread),
            Interpret(Guid.NewGuid(), "thrillers, no horror", ShowsThread));
    }

    /// <summary>
    /// The prompt version is a date so a stale cached answer can be dated at a glance, and so two
    /// branches editing the prompt in the same week collide in review rather than agreeing on "3".
    /// </summary>
    [Fact]
    public void The_prompt_generation_is_a_readable_dated_stamp()
    {
        Assert.StartsWith("askreel-", AskReelPromptVersion.Current, StringComparison.Ordinal);
        Assert.True(
            DateTime.TryParseExact(
                AskReelPromptVersion.Current["askreel-".Length..],
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _),
            $"'{AskReelPromptVersion.Current}' is not askreel-yyyy-MM-dd.");
    }
}
