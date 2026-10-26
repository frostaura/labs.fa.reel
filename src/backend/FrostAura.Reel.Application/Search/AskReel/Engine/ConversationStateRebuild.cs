using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The state of a conversation v2 has no current record of — a pre-v2 conversation on its first v2
/// turn, or one a v1 turn has since moved on — rebuilt from the turn record (§4.6).
///
/// <para><b>The defaults.</b> The first answer reads as <c>NewRequest</c> and later ones as
/// <c>Refine</c>, so the requirements are the newest answer's reading whose plan did not fail (each
/// v1 reading was a whole request, not a delta, so the newest one IS the merged state). The cards on
/// screen come from the endpoint's own projection of the record, handles from stored rank, and the
/// no-repeat set from every stored card — which the endpoint already passes as the shown set.</para>
///
/// <para><b>Legacy no-repeat entries.</b> A stored card whose title no longer resolves is translated
/// through <c>Titles</c> by its TMDB id; a translation with exactly one title is excluded by that
/// title, and an id that resolves to none — or to a movie AND a show, whose TMDB id spaces overlap —
/// stays excluded by its TMDB id, so nothing the user was shown can come back either way.</para>
/// </summary>
public static class ConversationStateRebuild
{
    public static async Task<ConversationState> RebuildAsync(
        IReelDbContext db, Guid conversationId, int answerNumber, IReadOnlyList<PresentedRef> presented, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var readings = await db.SearchTurns
            .Where(t => t.ConversationId == conversationId && t.Role == "assistant" && t.IntentJson != null)
            .OrderByDescending(t => t.Sequence)
            .Select(t => new { t.IntentJson, t.DegradedFlags })
            .Take(10)
            .ToListAsync(ct);
        var reading = readings.FirstOrDefault(r => !SearchTurnDegraded.PlanFailed(r.DegradedFlags));
        var intent = reading is null ? null : AskReelCarry.Parse(reading.IntentJson);

        var hard = intent is null ? HardConstraints.None : IntentConstraints.FromIntent(intent);
        var negations = intent is null
            ? []
            : (intent.ExcludeKeywords ?? []).Concat(intent.ExcludeGenres ?? [])
                .Select(n => n.Trim().ToLowerInvariant())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        var (legacyShown, legacyTmdb) = await LegacyAsync(db, conversationId, ct);
        return new ConversationState(
            Math.Max(0, answerNumber - 1),
            hard,
            SoftPreferences.None,
            negations,
            [],
            [],
            presented,
            legacyTmdb.Count == 0 ? null : legacyTmdb,
            ClarifiedOnce: false,
            legacyShown.Count == 0 ? null : legacyShown);
    }

    private static async Task<(List<Guid> Shown, List<long> Tmdb)> LegacyAsync(
        IReelDbContext db, Guid conversationId, CancellationToken ct)
    {
        var orphans = await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.TmdbId != null && !db.Titles.Any(t => t.Id == c.TitleId))
            .Select(c => c.TmdbId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (orphans.Count == 0)
        {
            return ([], []);
        }

        var matches = await db.Titles
            .Where(t => t.TmdbId != null && orphans.Contains(t.TmdbId.Value))
            .Select(t => new { t.Id, TmdbId = t.TmdbId!.Value })
            .ToListAsync(ct);
        var byTmdb = matches.GroupBy(m => m.TmdbId).ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToList());
        var shown = new List<Guid>();
        var tmdb = new List<long>();
        foreach (var id in orphans)
        {
            if (byTmdb.TryGetValue(id, out var titles) && titles.Count == 1)
            {
                shown.Add(titles[0]);
            }
            else
            {
                tmdb.Add(id);
            }
        }

        return (shown, tmdb);
    }
}
