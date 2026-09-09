using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Lens;

/// <summary>
/// The half of the lens guard chain that needs the database: does every person the copy names
/// actually work on this title, and has the account actually rated every title it compares against.
///
/// <para><b>Why the person check is framed as it is.</b> Detecting arbitrary invented names in free
/// prose is not solvable with a regex, so this does not try. It extracts candidate proper nouns,
/// intersects them with people the CATALOGUE knows, and refuses only when a known film person is
/// named who is not in this title's credits. That is precisely the failure mode worth catching —
/// "directed by Villeneuve" on a film he did not direct — and it produces essentially no false
/// positives, because a phrase the catalogue has never heard of is far more likely to be a
/// character from the overview than a fabricated credit.</para>
///
/// <para>The anchor check has no such difficulty and is therefore strict: an anchor title must
/// match something this account has rated, normalised for case and punctuation only.</para>
/// </summary>
public sealed class LensVerifier(IReelDbContext db)
{
    /// <summary>Candidate names are capped so a pathological note cannot turn into a large IN clause.</summary>
    private const int MaxCandidates = 24;

    /// <summary>
    /// Verifies one note. <paramref name="creditedNames"/> is the exact credit list the prompt was
    /// given for this title. Returns a rejection reason, or null when the note is grounded.
    /// </summary>
    public async Task<string?> VerifyAsync(
        Guid accountId, LensNote note, IReadOnlyCollection<string> creditedNames, CancellationToken ct)
    {
        var invented = await InventedPeopleAsync(note, creditedNames, ct);
        var unrated = await UnratedAnchorsAsync(accountId, note, ct);
        return LensGuards.InspectProvenance(invented, unrated);
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

    private async Task<IReadOnlyCollection<string>> UnratedAnchorsAsync(
        Guid accountId, LensNote note, CancellationToken ct)
    {
        if (note.Anchors.Count == 0)
        {
            return [];
        }

        var wanted = note.Anchors
            .Select(a => a.Title.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Matched case-insensitively in SQL: the model will write "sicario" or "Sicario" as the
        // sentence needs, and a case-sensitive IN would report a perfectly real anchor as invented.
        var wantedLower = wanted.Select(t => t.ToLowerInvariant()).ToList();

        // Rated, in this account, at whole-title level — the same subject types every other taste
        // read uses, because an episode rating is not evidence of an opinion about the show.
        var rated = (await db.UserRatings
                .Where(r => r.AccountId == accountId
                    && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                .Join(db.Titles, r => r.TitleId, t => t.Id, (_, t) => t.Name)
                .Where(name => wantedLower.Contains(name.ToLower()))
                .Distinct()
                .ToListAsync(ct))
            .Select(LensGuards.NormaliseTitle)
            .ToHashSet();

        return wanted
            .Where(t => !rated.Contains(LensGuards.NormaliseTitle(t)))
            .ToList();
    }
}
