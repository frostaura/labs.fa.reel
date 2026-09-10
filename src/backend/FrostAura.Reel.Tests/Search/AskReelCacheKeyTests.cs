using System.Reflection;
using FrostAura.Reel.Application.Search;
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
    public void The_rerank_fit_key_carries_the_prompt_generation_and_the_model()
    {
        var accountId = Guid.NewGuid();
        var titleId = Guid.NewGuid();
        var key = LiveSearchExpansionService.FitKey(accountId, titleId, "a tense thriller", "openai/gpt-5.4-mini");

        Assert.Contains(AskReelPromptVersion.Current, key, StringComparison.Ordinal);
        Assert.Contains("openai/gpt-5.4-mini", key, StringComparison.Ordinal);
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(accountId, titleId, "a tense thriller", "some/other-model"));
        // Still account- and title-scoped: a verdict is one person's, about one title.
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(Guid.NewGuid(), titleId, "a tense thriller", "openai/gpt-5.4-mini"));
        Assert.NotEqual(key, LiveSearchExpansionService.FitKey(accountId, Guid.NewGuid(), "a tense thriller", "openai/gpt-5.4-mini"));
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
