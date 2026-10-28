using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ingestion;

/// <summary>
/// Outcome of one hydration batch. <paramref name="Failed"/> carries the titles whose TMDB fetch
/// (or credit upsert) threw — they are left un-stamped so a caller can retry or retire them, while
/// everything else in the batch is already mutated and ready to save.
/// </summary>
/// <param name="Hydrated">Titles successfully hydrated (or confirmed missing at TMDB).</param>
/// <param name="Failed">Titles that threw and were skipped.</param>
public sealed record HydrationBatchResult(int Hydrated, IReadOnlyList<Title> Failed);

/// <summary>
/// Shared TMDB hydration core — artwork, popularity, trailer, credits (a show's creators included) — used by the catalog
/// hydration job (whole library) and the feed builder (top candidates need credits for
/// affinity features before final scoring). One hydration per title, ever, for every tenant.
/// </summary>
public class TitleHydrator(IReelDbContext db, ITmdbClient tmdb, ILogger<TitleHydrator> logger)
{
    private readonly Dictionary<long, Person> _personCache = [];

    /// <summary>
    /// ISO-3166-1 region used when a caller does not know the account's own, and the fallback for
    /// an account whose region TMDB has no certification for. US is the choice because it is by far
    /// the best-populated region in <c>release_dates</c> / <c>content_ratings</c>, and because
    /// <c>TasteMath.CertificationOrdinal</c> already reads MPAA and TV Parental Guidelines labels.
    /// </summary>
    public const string FallbackCertificationRegion = "US";

    /// <summary>
    /// Hydrates the given titles in place (caller saves); marks them refreshed either way.
    /// A title that throws is isolated and reported, never allowed to abort its batch.
    /// </summary>
    /// <param name="region">See <see cref="HydrateBatchResilientAsync"/>.</param>
    public async Task<int> HydrateBatchAsync(
        IReadOnlyList<Title> titles, CancellationToken ct, string? region = null) =>
        (await HydrateBatchResilientAsync(titles, ct, region)).Hydrated;

    /// <summary>
    /// As <see cref="HydrateBatchAsync"/>, but reports which titles failed so a caller that owns a
    /// cursor can count attempts and retire a permanently-broken title.
    ///
    /// Per-title isolation is the whole point: hydration used to run without a try/catch, so a
    /// single 429 / 5xx / malformed payload threw out of the loop before the caller's
    /// SaveChangesAsync, discarding the work already done for every healthy title in the batch.
    /// The retry then landed on the identical batch and hit the identical title — which is how
    /// ~28% of the catalogue ended up with LastMetadataRefreshAt still null (and, because
    /// EnrichCatalog scopes to hydrated titles, no embedding and no LLM attributes either).
    /// </summary>
    /// <param name="region">
    /// ISO-3166-1 region whose age certification is preferred, falling back to
    /// <see cref="FallbackCertificationRegion"/>. Optional because <c>Title</c> is a GLOBAL,
    /// cross-tenant row — one hydration serves every account — so certification cannot be per-account
    /// in the catalogue and the parameter only expresses which region's label to prefer when several
    /// exist. A caller that knows the account passes <c>account.Region</c>; the rest pass nothing.
    /// </param>
    public async Task<HydrationBatchResult> HydrateBatchResilientAsync(
        IReadOnlyList<Title> titles, CancellationToken ct, string? region = null)
    {
        var hydrated = 0;
        List<Title>? failed = null;

        foreach (var title in titles)
        {
            if (title.TmdbId is null)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                var details = title.MediaType == MediaType.Movie
                    ? await tmdb.GetMovieAsync(title.TmdbId.Value, ct)
                    : await tmdb.GetTvAsync(title.TmdbId.Value, ct);

                if (details is not null)
                {
                    Apply(title, details, region);
                    await UpsertCreditsAsync(title, details, ct);
                }

                title.LastMetadataRefreshAt = DateTime.UtcNow; // hydrated OR confirmed-missing: never retry forever
                hydrated++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A real shutdown/cancel is not a title defect — let it unwind.
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately broad: an HTTP timeout surfaces as TaskCanceledException with the
                // token un-cancelled, and a malformed payload as JsonException. Neither may be
                // allowed to cost the rest of the batch its already-completed work.
                (failed ??= []).Add(title);
                logger.LogWarning(
                    ex,
                    "TMDB hydration failed for title {TitleId} (tmdb {TmdbId}); skipping it and continuing the batch.",
                    title.Id,
                    title.TmdbId);
            }
        }

        return new HydrationBatchResult(hydrated, (IReadOnlyList<Title>?)failed ?? []);
    }

    /// <summary>
    /// Picks the certification for <paramref name="region"/>, falling back to
    /// <see cref="FallbackCertificationRegion"/>, or null when TMDB has neither.
    /// </summary>
    internal static string? ResolveCertification(
        IReadOnlyDictionary<string, string>? byRegion, string? region)
    {
        if (byRegion is null || byRegion.Count == 0)
        {
            return null;
        }

        var preferred = string.IsNullOrWhiteSpace(region)
            ? FallbackCertificationRegion
            : region.Trim().ToUpperInvariant();

        return byRegion.TryGetValue(preferred, out var exact) ? exact
            : byRegion.TryGetValue(FallbackCertificationRegion, out var fallback) ? fallback
            : null;
    }

    private static void Apply(Title title, TmdbTitleDetails details, string? region)
    {
        title.PosterPath = details.PosterPath ?? title.PosterPath;
        title.BackdropPath = details.BackdropPath ?? title.BackdropPath;
        title.TmdbPopularity = details.Popularity;
        // Only a film carries a collection; a details read that names none clears a stale one.
        title.TmdbCollectionId = details.Collection?.Id;
        title.TmdbVoteAverage = details.VoteAverage;
        title.TmdbVoteCount = details.VoteCount;
        // Language — the only signal for discovery titles (Trakt sets it for library titles via
        // IngestMapper). Powers the language allow-list / English-only default, so a wrong value is
        // directly visible to the user: TMDB claims original_language "en" for the Spanish telenovela
        // "El final del paraíso" (tv/95439) and it walked straight into an English-only feed.
        // TmdbLanguage.Resolve corroborates original_language against the spoken-language list rather
        // than trusting one field.
        var language = TmdbLanguage.Resolve(details.OriginalLanguage, details.SpokenLanguages, details.Languages);
        if (language is not null)
        {
            title.Language = language;
        }
        title.RuntimeMinutes ??= details.Runtime;
        // Certification — TRAKT STAYS AUTHORITATIVE. Only a title with no certification at all gets
        // TMDB's, which in practice means every TMDB-discovered candidate (Trakt never saw it) and
        // the tail of library titles Trakt has no rating for. Filling it here is what makes
        // `certificationOrdinal` and `hasCertification` mean the same thing on a candidate as on a
        // training row; before this the feature was, in effect, "did this title come from the user's
        // own library" — a label proxy dressed as a content attribute.
        //
        // Side effect worth knowing: EligibilityQueryBuilder.PassesMaturity treats an unknown
        // certification as passing only under a permissive ceiling, so a restricted profile now sees
        // correctly-rated candidates it used to drop AND correctly loses over-ceiling ones it used to
        // keep. Both directions are the rule finally working on discovered titles.
        if (string.IsNullOrWhiteSpace(title.Certification))
        {
            title.Certification = ResolveCertification(details.CertificationsByRegion, region);
        }
        title.Overview ??= details.Overview;
        title.Tagline ??= details.Tagline;
        title.AiredEpisodes ??= details.NumberOfEpisodes;
        if (details.Keywords.Length > 0)
        {
            title.Keywords = details.Keywords;
        }

        // TMDB's "TV Movie" genre, which nothing else in Reel can see (ADD-18). Set and cleared
        // only from TMDB's own list, so the refresh sweep keeps it current.
        if (title.MediaType == MediaType.Movie)
        {
            title.Genres = TvMovieGenre.Apply(title.Genres, details.GenreIds);
        }

        // Release dates for TMDB-discovered rows, which Trakt never described. Trakt stays
        // authoritative where it has spoken; the refresh sweep reads these to find recent releases.
        if (title.MediaType == MediaType.Movie)
        {
            title.ReleasedAt ??= details.ReleasedAt;
        }
        else
        {
            title.FirstAiredAt ??= details.ReleasedAt;
        }
        if (details.TrailerYouTubeKey is not null)
        {
            title.TrailerUrl = $"https://youtube.com/watch?v={details.TrailerYouTubeKey}";
        }
    }

    private async Task UpsertCreditsAsync(Title title, TmdbTitleDetails details, CancellationToken ct)
    {
        if (details.Cast.Length == 0 && details.Crew.Length == 0 && details.Creators.Count == 0)
        {
            return;
        }

        var creditKeys = (await db.TitleCredits
                .Where(c => c.TitleId == title.Id)
                .Select(c => new { c.PersonId, c.Role })
                .ToListAsync(ct))
            .Select(c => (c.PersonId, c.Role))
            .ToHashSet();

        await PrimePersonCacheAsync(details, ct);

        foreach (var member in details.Cast)
        {
            var person = GetOrAddPerson(member.Id, member.Name, member.KnownForDepartment, member.ProfilePath);
            AddCredit(title, person, CreditRole.Actor, member.Order, member.Character, creditKeys);
        }

        foreach (var member in details.Crew)
        {
            var role = member.Job == "Director" ? CreditRole.Director : CreditRole.Writer;
            var person = GetOrAddPerson(member.Id, member.Name, member.Department, member.ProfilePath);
            AddCredit(title, person, role, null, null, creditKeys);
        }

        // A show's creators (HREV2-D-02), in TMDB's order — the person the show is "by". A show's crew
        // names a director per episode, so without these Breaking Bad's page listed thirteen episode
        // directors and never Vince Gilligan as its creator. TMDB sends no created_by for a film.
        for (var order = 0; order < details.Creators.Count; order++)
        {
            var creator = details.Creators[order];
            if (creator.Id <= 0 || string.IsNullOrWhiteSpace(creator.Name))
            {
                continue;
            }

            var person = GetOrAddPerson(creator.Id, creator.Name, null, creator.ProfilePath);
            AddCredit(title, person, CreditRole.Creator, order, null, creditKeys);
        }
    }

    /// <summary>
    /// Resolves every not-yet-cached person of this title in ONE round-trip. It used to be a
    /// FirstOrDefaultAsync per unique person — up to two dozen single-row queries per title, on
    /// every title in the library.
    /// </summary>
    private async Task PrimePersonCacheAsync(TmdbTitleDetails details, CancellationToken ct)
    {
        var wanted = details.Cast.Select(c => c.Id)
            .Concat(details.Crew.Select(c => c.Id))
            .Concat(details.Creators.Select(c => c.Id))
            .Distinct()
            .Where(id => !_personCache.ContainsKey(id))
            .ToArray();
        if (wanted.Length == 0)
        {
            return;
        }

        var known = await db.Persons.Where(p => wanted.Contains(p.TmdbId)).ToListAsync(ct);
        foreach (var person in known)
        {
            _personCache[person.TmdbId] = person;
        }
    }

    /// <summary>Cache hit, or a brand-new person — <see cref="PrimePersonCacheAsync"/> already asked the database.</summary>
    private Person GetOrAddPerson(long tmdbId, string name, string? department, string? profilePath)
    {
        if (_personCache.TryGetValue(tmdbId, out var cached))
        {
            return cached;
        }

        var person = new Person { Id = Guid.NewGuid(), TmdbId = tmdbId, Name = name, KnownForDepartment = department, ProfilePath = profilePath };
        db.Persons.Add(person);
        _personCache[tmdbId] = person;
        return person;
    }

    private void AddCredit(Title title, Person person, CreditRole role, int? castOrder, string? character, HashSet<(Guid, CreditRole)> creditKeys)
    {
        if (!creditKeys.Add((person.Id, role)))
        {
            return;
        }

        db.TitleCredits.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = title.Id,
            PersonId = person.Id,
            Role = role,
            CastOrder = castOrder,
            CharacterName = character,
        });
    }
}
