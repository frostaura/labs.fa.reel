using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Lens;

/// <summary>
/// Assembles the per-title half of a lens prompt, and the provenance sets the guards check the
/// answer against.
///
/// <para>Every field here already exists in the catalogue and has already been paid for. Tone, era,
/// themes and the seven scalars in particular were extracted per title by a paid model and then
/// read by the feature vector and almost nothing else — this is the surface where that spend
/// finally reaches something a person reads.</para>
///
/// <para><b>People are restricted before the prompt is built, not after.</b> The credits list the
/// model is shown IS the list it is allowed to name, so the provenance check downstream is a
/// verification of a promise rather than a rescue.</para>
/// </summary>
public sealed class LensInputBuilder(IReelDbContext db, RatingCalibrationService calibration)
{
    /// <summary>Cast members named in the prompt. Beyond three the list stops being a signal.</summary>
    public const int TopCast = 3;

    /// <summary>Keywords passed through. The extractor uses the same cap for the same reason.</summary>
    public const int MaxKeywords = 20;

    /// <summary>
    /// The title inputs plus, per title, the exact set of people the copy may name — the union of
    /// the credits shown and nothing else.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<LensTitleInput> Titles,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> CreditedNames);

    public async Task<Result> BuildAsync(Guid accountId, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Result([], new Dictionary<Guid, IReadOnlyCollection<string>>());
        }

        var titles = await db.Titles
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var attributes = await db.TitleAttributes
            .Where(a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .ToDictionaryAsync(a => a.TitleId, ct);

        var credits = await db.TitleCredits
            .Where(c => ids.Contains(c.TitleId))
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.Role, c.CastOrder, p.Name, c.CharacterName })
            .ToListAsync(ct);
        var creditsByTitle = credits
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.TitleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // The prediction rides the prompt as CONTEXT only, and its error bar with it, so the model
        // knows how confidently to pitch. Neither may be restated — that is what keeps the model
        // artifact out of the cache key and the cache warm across a retrain.
        var scores = await db.TitleScores
            .Where(s => s.AccountId == accountId && ids.Contains(s.TitleId))
            .Select(s => new { s.TitleId, s.PredictedRating, s.ScoredAt })
            .ToListAsync(ct);
        var predicted = scores
            .GroupBy(s => s.TitleId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.ScoredAt).First().PredictedRating);
        var calibrator = await calibration.GetAsync(accountId, ct);
        var error = calibrator.PairCount > 0 ? (decimal?)Math.Round(calibrator.ExpectedAbsoluteError, 1) : null;

        var inputs = new List<LensTitleInput>(ids.Count);
        var creditedNames = new Dictionary<Guid, IReadOnlyCollection<string>>();

        foreach (var id in ids)
        {
            if (!titles.TryGetValue(id, out var title))
            {
                continue;
            }

            var titleCredits = creditsByTitle.GetValueOrDefault(id) ?? [];
            var director = titleCredits
                .Where(c => c.Role == CreditRole.Director)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => c.Name)
                .FirstOrDefault();
            var writer = titleCredits
                .Where(c => c.Role == CreditRole.Writer)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => c.Name)
                .FirstOrDefault();
            var cast = titleCredits
                .Where(c => c.Role == CreditRole.Actor)
                .OrderBy(c => c.CastOrder ?? int.MaxValue)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => c.Name)
                .Take(TopCast)
                .ToList();

            attributes.TryGetValue(id, out var attrs);

            inputs.Add(new LensTitleInput(
                title.Id,
                title.Name,
                title.Year,
                title.MediaType.ToString(),
                title.RuntimeMinutes,
                title.Genres,
                // Only a closed-vocabulary tone is trusted: rows written before enforcement carry
                // free-text labels, and handing one to a writer teaches it a taxonomy that does not exist.
                AttributeVocabulary.IsClosedVocabTone(attrs?.Tone) ? attrs!.Tone : null,
                attrs?.Era,
                attrs?.Themes ?? [],
                attrs?.Darkness,
                attrs?.Pacing,
                attrs?.Complexity,
                attrs?.EmotionalIntensity,
                attrs?.Humor,
                attrs?.Optimism,
                attrs?.EnsembleVsSolo,
                director,
                writer,
                cast,
                title.Overview,
                title.Keywords.Take(MaxKeywords).ToList(),
                predicted.TryGetValue(id, out var p) ? Ranking.ForYouBar.Shown(p) : null,
                error));

            // Exactly the names the prompt showed — the provenance check is then a verification
            // that the model stayed inside what it was given, not a lookup against the whole catalogue.
            // That includes the people and places the OVERVIEW names, and the credited characters:
            // "David Dunn" in a lens on Glass is the overview's character, and the catalogue also
            // holds a real person of that name, so without this the check refused sound copy as an
            // invented credit (slot run, 2026-10-26).
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in titleCredits.Select(c => c.Name)
                         .Concat(titleCredits.Select(c => c.CharacterName).OfType<string>().Where(n => n.Length > 0))
                         .Concat(LensGuards.CandidateNames(title.Overview)))
            {
                names.Add(name);
            }

            creditedNames[id] = names;
        }

        return new Result(inputs, creditedNames);
    }
}
