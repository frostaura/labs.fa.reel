using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Lens;

/// <summary>
/// What the provenance pass decided: the note as it should be STORED, why it was refused if it
/// was, and the anchor entries that were dropped on the way.
/// </summary>
/// <param name="Note">The note with unresolvable anchors removed. Identical to the draft when none were.</param>
/// <param name="Reason">The rejection reason, with the offending token appended, or null.</param>
/// <param name="StrippedAnchors">Anchor titles that did not resolve and were dropped. Logged, never rendered.</param>
public sealed record LensVerdict(LensNote Note, string? Reason, IReadOnlyList<string> StrippedAnchors)
{
    public bool IsRejected => Reason is not null;
}

/// <summary>
/// The half of the lens guard chain that needs the database: does every person the copy names
/// actually work on this title, and does every title it invokes actually exist in this account's
/// library.
///
/// <para><b>Why the person check is framed as it is.</b> Detecting arbitrary invented names in free
/// prose is not solvable with a regex, so this does not try. It extracts candidate proper nouns,
/// intersects them with people the CATALOGUE knows, and refuses only when a known film person is
/// named who is not in this title's credits. That is precisely the failure mode worth catching —
/// "directed by Villeneuve" on a film he did not direct — and it produces essentially no false
/// positives, because a phrase the catalogue has never heard of is far more likely to be a
/// character from the overview than a fabricated credit.</para>
///
/// <para><b>The anchor policy, and why it is no longer "strict".</b> The previous rule was that an
/// anchor must equal, case- and punctuation-insensitively, the name of a title the account had
/// RATED. It rejected every on-demand lens the founder's account produced, and it did so for three
/// compounding reasons, each of which is fixed here.</para>
///
/// <para><i>One: the prompt showed the model a set of titles it then refused to accept.</i> The
/// only title names in a lens prompt were the ones inside the declined line — titles the account
/// dismissed from the feed WITHOUT rating them. The model, told to take anchors "verbatim from the
/// lists below", took them from the only list there. Every one failed a rated-only check. So the
/// library here is rated ∪ watched ∪ reacted: an account that dismissed a film has an opinion about
/// it, which is exactly what an anchor needs, and it is what the prompt itself put in front of the
/// model.</para>
///
/// <para><i>Two: the normalisation was dead code.</i> The old query filtered library rows to those
/// whose lowercased name was already in the wanted set, and only then normalised — so
/// normalisation could never rescue a name that had not already matched exactly. "The Amateur" for
/// a row called "Amateur" failed with the normaliser sitting right beside it. The library is now
/// indexed in memory on the normalised keys, so normalisation actually runs.</para>
///
/// <para><i>Three: an unresolvable anchor sank the whole note.</i> An anchor is a structured field
/// the surface can simply omit. Dropping one entry costs a comparison; refusing the note costs the
/// four sentences of prose that were the point. So a failed anchor is STRIPPED and logged, and
/// only PROSE-level invention — a person outside the credits, or a title outside the library named
/// in <c>lens</c>, <c>hook</c> or <c>caution</c> — is fatal.</para>
/// </summary>
public sealed class LensVerifier(IReelDbContext db)
{
    /// <summary>Candidate names are capped so a pathological note cannot turn into a large IN clause.</summary>
    private const int MaxCandidates = 24;

    /// <summary>
    /// Verifies one note. <paramref name="creditedNames"/> is the exact credit list the prompt was
    /// given for this title.
    /// </summary>
    public async Task<LensVerdict> VerifyAsync(
        Guid accountId, LensNote note, IReadOnlyCollection<string> creditedNames, CancellationToken ct)
    {
        var library = await LibraryIndexAsync(accountId, ct);

        var kept = new List<LensAnchor>(note.Anchors.Count);
        var stripped = new List<string>();
        foreach (var anchor in note.Anchors)
        {
            var name = anchor.Title.Trim();
            if (name.Length > 0 && LensGuards.MatchesLibrary(name, library))
            {
                kept.Add(anchor);
            }
            else if (name.Length > 0)
            {
                stripped.Add(name);
            }
        }

        var trimmed = kept.Count == note.Anchors.Count ? note : note with { Anchors = kept };

        // Prose is checked on the TRIMMED note: an anchor that has been dropped is no longer part
        // of the copy, and its relation clause must not be able to convict the note it left.
        var invented = await InventedPeopleAsync(trimmed, creditedNames, ct);
        var inventedTitles = await InventedProseTitlesAsync(trimmed, creditedNames, library, ct);
        var reason = LensGuards.InspectProvenance(invented, inventedTitles);

        return new LensVerdict(trimmed, reason, stripped);
    }

    /// <summary>
    /// Every comparison key for every title this account has an opinion about — rated at
    /// whole-title level, watched, or reacted to in the feed.
    ///
    /// <para>Episode and season ratings are excluded from the RATED half for the same reason every
    /// other taste read excludes them: an episode rating is not evidence of an opinion about the
    /// show. Watching one, however, is — so a show reached only through its episodes still arrives
    /// through <see cref="IReelDbContext.WatchedTitles"/>.</para>
    /// </summary>
    private async Task<HashSet<string>> LibraryIndexAsync(Guid accountId, CancellationToken ct)
    {
        var rated = db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => r.TitleId);
        var watched = db.WatchedTitles
            .Where(w => w.AccountId == accountId)
            .Select(w => w.TitleId);
        var reacted = db.UserTitleReactions
            .Where(x => x.AccountId == accountId && x.RevokedAt == null)
            .Select(x => x.TitleId);

        var names = await db.Titles
            .Where(t => rated.Contains(t.Id) || watched.Contains(t.Id) || reacted.Contains(t.Id))
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync(ct);

        return LensGuards.BuildTitleIndex(names);
    }

    private async Task<IReadOnlyCollection<string>> InventedPeopleAsync(
        LensNote note, IReadOnlyCollection<string> creditedNames, CancellationToken ct)
    {
        var candidates = LensGuards.CandidateNames(note)
            .Where(n => !creditedNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Take(MaxCandidates)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        // Persons is a global catalogue table — no tenancy filter to fight, and the lookup is by
        // exact name so it cannot accidentally match a substring of an unrelated person.
        return await db.Persons
            .Where(p => candidates.Contains(p.Name))
            .Select(p => p.Name)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Titles named in the PROSE that this account's library does not contain — the fatal half of
    /// the anchor rule.
    ///
    /// <para>Framed exactly as the person check is, and for the same reason: a capitalised phrase
    /// the CATALOGUE has never heard of is far more likely to be a character or a place from the
    /// overview than a fabricated film, so only phrases the catalogue knows as real titles are
    /// convictable. Anchor relations are excluded from the scan — they are attached to an entry
    /// that has already been resolved or already been stripped — as are the title's own name and
    /// its credited people, which are legitimately in the prose.</para>
    /// </summary>
    private async Task<IReadOnlyCollection<string>> InventedProseTitlesAsync(
        LensNote note,
        IReadOnlyCollection<string> creditedNames,
        IReadOnlyCollection<string> library,
        CancellationToken ct)
    {
        var proseOnly = note with { Anchors = [] };
        var candidates = LensGuards.CandidateNames(proseOnly)
            .Where(n => !creditedNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Where(n => !LensGuards.MatchesLibrary(n, library))
            .Take(MaxCandidates)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var known = await db.Titles
            .Where(t => candidates.Contains(t.Name))
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync(ct);

        // Re-checked against the library after the catalogue lookup: the catalogue's own spelling
        // may differ from the model's, and a title the account owns must never convict it.
        return known.Where(n => !LensGuards.MatchesLibrary(n, library)).ToList();
    }
}
