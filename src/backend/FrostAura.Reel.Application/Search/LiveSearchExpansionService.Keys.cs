using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's cache keys: the discovery pull, the interpretation, the judge's verdicts and the concept vectors.
/// v1 only; deleted at cutover (§4.13).
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// The identity of a TMDB discovery pull: every field that changes the query TMDB is asked, and
    /// nothing else.
    ///
    /// <para><b>Every argument of the discover call has to appear here</b>, positive and negative
    /// alike. The exclusions do not merely re-order a pull — they ride as <c>without_genres</c> /
    /// <c>without_keywords</c> and change which rows come back, so a key that omitted them served
    /// "a tense thriller, no horror" the cached, unexcluded answer to "a tense thriller". Region is
    /// here for a second reason on top of that: the single-flight key is derived from this string,
    /// so two accounts in different regions asking the same thing at the same moment would share one
    /// fetch and one of them would get the other's catalogue.</para>
    ///
    /// <para><see cref="ReferenceTitles"/> is deliberately absent, and it is the only field that is:
    /// references are resolved separately and blended into the RANKING vector, never handed to
    /// discovery, so two turns differing only in what they name as a comparison genuinely do share a
    /// pull. <c>AskReelCacheKeyTests</c> holds that reasoning to account by reflection.</para>
    /// </summary>
    internal static string IntentKey(SearchIntent intent, string? region) => string.Create(
        CultureInfo.InvariantCulture,
        $"{string.Join(',', intent.Genres.OrderBy(g => g, StringComparer.Ordinal))}|" +
        $"{string.Join(',', intent.Keywords.OrderBy(k => k, StringComparer.Ordinal))}|" +
        $"{string.Join(',', intent.MediaTypes.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal))}|" +
        $"{intent.MinYear}|{intent.MaxYear}|{intent.MaxRuntimeMinutes}|{NormalizeQuery(intent.FreeText)}|" +
        $"{string.Join(',', (intent.People ?? []).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name}:{p.Role}")).OrderBy(p => p, StringComparer.Ordinal))}|" +
        $"-{string.Join(',', (intent.ExcludeGenres ?? []).OrderBy(g => g, StringComparer.Ordinal))}|" +
        $"-{string.Join(',', (intent.ExcludeKeywords ?? []).OrderBy(k => k, StringComparer.Ordinal))}|" +
        $"@{region}");

    /// <summary>
    /// The identity of one INTERPRETATION: everything that changes what the interpret call is asked,
    /// and nothing that does not.
    ///
    /// <para>The account is on the key and is not optional. This is a process-wide
    /// <see cref="IMemoryCache"/> in a multi-tenant app, and the cached value is a REPLY written
    /// against one person's taste summary — handing it to somebody else asking the same thing would
    /// be a cross-tenant leak in a product whose entire premise is per-user. The standing-filter
    /// summary rides in whole rather than hashed: it is already short, sorted and deterministic
    /// (<see cref="AskReelStandingFilters.Describe"/>), and a hash would trade readability for a
    /// collision class nobody could debug.</para>
    ///
    /// <para><b>The CONVERSATION is on the key, and leaving it off was a defect that shipped.</b>
    /// The interpret prompt is handed the last turns of the transcript, so the interpretation is a
    /// function of the question AND the thread it was asked in — not of the question alone. An
    /// earlier version of this key omitted the history on the reasoning that it "moves on every
    /// turn", which is true and beside the point: a key that omits an input does not merely miss
    /// less often, it answers a DIFFERENT question confidently for the whole TTL. The observed
    /// failure was exactly that. A thread where the user had said "only shows" resolved the next
    /// sentence, "thrillers, no horror", to <c>MediaTypes: [Show]</c> — correct, history shaped it.
    /// The same user then pressed + New, typed the identical sentence into a thread with no history
    /// at all, and was served the first thread's interpretation: same <c>IntentJson</c>, a
    /// byte-identical reply promising "shows", and an all-TV answer to a question that had asked for
    /// nothing of the kind. <c>AskReelConversationIsolationTests</c> is that turn, pinned.</para>
    ///
    /// <para>The <b>presented handles</b> are on the key for the same reason: they reach the prompt
    /// as the numbered list "the second one" resolves against, so they change what a pronoun in the
    /// question means. They ride as <see cref="AskReelHandles.Digest"/> — every handle and its title
    /// id, in prompt order — and the conversation's whole no-repeat set rides beside them as a digest
    /// of title ids, because it changes what the model is told not to return.</para>
    ///
    /// <para>Both ride as a SHA-256 digest rather than in full, because a transcript is unbounded
    /// and a cache key is not; the standing-filter summary still rides whole because it is short,
    /// sorted and deterministic (<see cref="AskReelStandingFilters.Describe"/>) and a hash would
    /// trade readability for a collision class nobody could debug. "No history" and "nothing shown"
    /// are their own literal tokens rather than the digest of an empty string, so the commonest
    /// context in the product — a brand-new thread — is a value a person can read in a dump.</para>
    ///
    /// <para><b>What this deliberately does NOT cost.</b> The reason the cache exists is a
    /// byte-identical repeat: one question asked twice inside the window used to re-buy the
    /// interpret call and then re-pull TMDB behind a drifted intent, ~21 s against ~2.5 s. A repeat
    /// in the SAME context still hits, because the same transcript digests to the same value. What
    /// no longer hits is a repeat in a different context, which was never the same question.
    /// Note also what is NOT used here: the conversation id. It would close the leak and throw the
    /// win away, because two threads with identical (empty) transcripts would stop sharing an
    /// entry. The key is the context, not the container.</para>
    ///
    /// <para>The remaining prompt input that is not on the key is the taste summary, and that is a
    /// judgement rather than an oversight: it is one person's, the account is already on the key,
    /// and it can only move inside a ten-minute <see cref="InterpretTtl"/> if that person rates
    /// something mid-conversation. The cost of being wrong is one slightly stale reply for one
    /// account for under ten minutes; the cost of keying on it is a miss on every turn after any
    /// rating. Revisit it if the TTL grows.</para>
    /// </summary>
    internal static string InterpretKey(
        Guid accountId,
        string query,
        string? standingSummary,
        IReadOnlyList<MediaType> mediaFilter,
        string modelId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<PresentedCard> presented,
        IReadOnlyCollection<Guid> shownTitleIds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"askreel:intent:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{NormalizeQuery(query)}|" +
            $"{string.Join(',', mediaFilter.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal))}|" +
            $"{standingSummary}|h={HistoryDigest(history)}|p={AskReelHandles.Digest(presented)}|s={ShownDigest(shownTitleIds)}");

    /// <summary>
    /// A stable digest of the transcript the interpreter is handed — every turn, in order, role and
    /// text.
    ///
    /// <para><b>Order is preserved and never sorted.</b> "only shows" then "thrillers, no horror" is
    /// a different conversation from the reverse, and the model reads them as a sequence.</para>
    ///
    /// <para><b>Every field is length-prefixed</b> before it is hashed, which is the whole
    /// difference between a digest and a concatenation. Without it <c>["ab", ""]</c> and
    /// <c>["a", "b"]</c> flatten to the same bytes, and two different transcripts share a cache
    /// entry again — the identical defect, one layer down and far harder to see. The unit separators
    /// are control characters no transcript contains, and the lengths are formatted invariantly like
    /// every other number this backend writes.</para>
    ///
    /// <para>The WHOLE history is digested, not the slice the prompt keeps (the adapter takes the
    /// last 8, and the endpoint reads at most 8 rows, so today they are the same list). Digesting
    /// the superset can only ever cost an extra miss where two transcripts differ solely in turns
    /// the prompt would have dropped; digesting a guess at the adapter's window would cost a leak
    /// the day that window changes, and this file cannot see that constant.</para>
    /// </summary>
    internal static string HistoryDigest(IReadOnlyList<ChatTurn> history)
    {
        if (history.Count == 0)
        {
            // Its own token, not the digest of an empty string: "a thread with no history" is the
            // most common context in the product and is worth being able to read in a cache dump.
            return "none";
        }

        var sb = new StringBuilder();
        foreach (var turn in history)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{turn.Role.Length}\u001f{turn.Role}\u001f{turn.Text.Length}\u001f{turn.Text}\u001e"));
        }

        return Digest(sb.ToString());
    }

    /// <summary>
    /// A stable digest of the conversation's no-repeat set.
    ///
    /// <para>Sorted and de-duplicated first, because the set is what the prompt is a function of and
    /// the endpoint's <c>Distinct()</c> read carries no ordering guarantee from Postgres — keying on
    /// the raw row order would turn one context into arbitrarily many keys and quietly cost the
    /// repeat hit. Keyed on the TITLE id, like the set itself (B-16).</para>
    /// </summary>
    internal static string ShownDigest(IReadOnlyCollection<Guid> shownTitleIds)
    {
        if (shownTitleIds.Count == 0)
        {
            return "none";
        }

        return Digest(string.Join(
            ',',
            shownTitleIds.Distinct().OrderBy(id => id).Select(id => id.ToString("N"))));
    }

    /// <summary>
    /// SHA-256, lower-case hex. Not a security boundary — it is a fixed-width, collision-resistant
    /// stand-in for an unbounded string in a cache key, chosen over <c>string.GetHashCode</c>
    /// (randomised per process, so a key would not survive a restart and could not be reasoned about
    /// across a fleet) and over the raw text (a transcript has no length bound).
    /// </summary>
    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// The discovered-id cache key. Carries the prompt generation and the model id because the ids
    /// cached under it are the ids a PARTICULAR interpretation of the turn produced — edit the
    /// prompt or change the model and the same sentence resolves to a different intent, which is
    /// precisely the case the author will re-test with and the one a version-less key would answer
    /// from cache for the next six hours. Same lever as <c>TitleLensCopy</c>'s prompt version.
    /// </summary>
    internal static string CacheKey(string intentKey, string modelId) => string.Create(
        CultureInfo.InvariantCulture, $"askreel:ids:{AskReelPromptVersion.Current}:{modelId}:{intentKey}");

    /// <summary>
    /// The per-(account, title, query) re-rank verdict cache key, versioned for the same reason
    /// <see cref="CacheKey"/> is: a fit and a why-line are the output of one prompt against one
    /// model, and serving a six-hour-old verdict from the previous prompt is how a prompt fix looks
    /// like it did nothing.
    /// </summary>
    internal static string FitKey(string salt, Guid accountId, Guid titleId, string qnorm, IEnumerable<Guid> batch) =>
        AskReelJudge.FitKey(salt, accountId, titleId, qnorm, batch);

    /// <summary>
    /// The semantic arm's concept-embedding key: the account and the exact text the provider would
    /// be paid to embed.
    ///
    /// <para>The CONCEPT TEXT rather than the intent, because it is literally the provider's
    /// argument: two intents that render the same concept are the same call, and one that renders a
    /// different concept is a different vector however similar the intents look.</para>
    ///
    /// <para>The ACCOUNT is on it for the same reason it is on <see cref="InterpretKey"/>, even
    /// though an embedding is a pure function of its text and could in principle be shared: this is
    /// a process-wide <see cref="IMemoryCache"/> in a multi-tenant app, the concept is assembled
    /// from a reply written against one person's taste, and the cost of keeping the boundary is one
    /// duplicate embedding in the rare case two tenants type a byte-identical concept. Cheap
    /// insurance beats a tenancy argument that has to be re-made every time someone reads this.</para>
    /// </summary>
    internal static string ConceptVectorKey(Guid accountId, string concept, string modelId) => string.Create(
        CultureInfo.InvariantCulture,
        $"askreel:concept:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{NormalizeQuery(concept)}");
}
