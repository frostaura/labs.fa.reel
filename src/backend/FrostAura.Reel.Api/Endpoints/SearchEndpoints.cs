using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAccount();

        // Typeahead with the personal lens: watched titles appear (badged), content filters and the
        // maturity ceiling are airtight, and the shortlist is scored LIVE against the Active model so
        // the best-for-you matches float up rather than the merely-popular ones.
        group.MapGet("/typeahead", async (
            string q, IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            {
                return Results.Ok(new { titles = Array.Empty<object>(), people = Array.Empty<object>() });
            }

            var term = q.Trim();
            var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);

            // ContentFilteredTitles (not EligibleTitles): watched titles legitimately appear here
            // badged — but every exclusion still applies, airtight. Over-fetch, then apply the
            // in-memory maturity gate (certifications don't order in SQL) and keep 8, so a
            // restricted profile still lands a full shortlist instead of a starved one.
            //
            // Match on substring OR trigram similarity. Substring alone is exact-spelling-only:
            // "inceptoin" and "spiderman" both returned literally nothing, because ILIKE '%term%'
            // cannot see past a transposed letter or a missing hyphen. pg_trgm has been installed
            // and declared in every migration since the project started — and is half the reason
            // this project deviates from the house postgres image to pgvector/pgvector:pg16 — but
            // nothing ever queried it. Exact substring still wins the ordering, so a correctly
            // spelled query is unaffected; fuzzy matches only fill in behind it.
            var matched = await eligibility.ContentFilteredTitles(accountId)
                .Where(t => EF.Functions.ILike(t.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(t.Name, term))
                .Where(t => !db.UserTitleReactions.Any(r =>
                    r.AccountId == accountId && r.TitleId == t.Id && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null))
                .OrderByDescending(t => EF.Functions.ILike(t.Name, $"%{term}%"))
                .ThenByDescending(t => EF.Functions.TrigramsSimilarity(t.Name, term))
                .ThenByDescending(t => t.TmdbPopularity)
                .Take(24)
                .ToListAsync(ct);

            var candidates = EligibilityQueryBuilder.WithinMaturity(matched, maturityCeiling).Take(8).ToList();
            var candidateIds = candidates.Select(t => t.Id).ToList();

            // Relevance tier per candidate, recomputed here because the SQL ordering is lost the
            // moment these are re-sorted by predicted rating below. 2 = the term appears verbatim,
            // 1 = a close fuzzy match, 0 = a distant one.
            //
            // This exists because fuzzy matching widened the candidate set: before trigram search,
            // every row was an exact substring match and sorting the survivors purely by predicted
            // rating was harmless. Now a loosely-similar title can outrank the one the user
            // actually typed — "spiderman" surfaced Superman above Spider-Man, because Superman
            // simply scored better for this account. Relevance has to dominate for a LOOKUP;
            // the personal lens is the tiebreak within a tier, not the primary key.
            var tiers = candidates.ToDictionary(
                t => t.Id,
                t => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2
                    : TitleSimilarity(t.Name, term) >= 0.5 ? 1
                    : 0);

            // Live per-user scoring of the ≤8 survivors against the Active artifact (cheap: one model
            // load + one taste build). No model yet ⇒ empty map ⇒ order falls back to popularity.
            var predicted = await scorer.ScoreAsync(accountId, candidateIds, DateTime.UtcNow, ct);
            var watchedFull = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && candidateIds.Contains(w.TitleId) && w.IsFullyWatched)
                .Select(w => w.TitleId)
                .ToListAsync(ct)).ToHashSet();

            var titles = candidates
                .OrderByDescending(t => tiers[t.Id])
                .ThenByDescending(t => predicted.TryGetValue(t.Id, out var p) ? p : decimal.MinValue)
                .ThenByDescending(t => t.TmdbPopularity ?? 0)
                .Select(t => new
                {
                    titleId = t.Id,
                    mediaType = t.MediaType.ToString(),
                    tmdbId = t.TmdbId,
                    name = t.Name,
                    year = t.Year,
                    posterPath = t.PosterPath,
                    isFullyWatched = watchedFull.Contains(t.Id),
                    predictedRating = predicted.TryGetValue(t.Id, out var p) ? (decimal?)p : null,
                    crowdRating = CrowdRating.For(t),
                    crowdVotes = CrowdRating.VotesFor(t),
                })
                .ToList();

            var people = await db.Persons
                .Where(p => EF.Functions.ILike(p.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(p.Name, term))
                .OrderByDescending(p => EF.Functions.ILike(p.Name, $"%{term}%"))
                .ThenByDescending(p => EF.Functions.TrigramsSimilarity(p.Name, term))
                .Take(4)
                .Select(p => new { personId = p.Id, name = p.Name, knownFor = p.KnownForDepartment, profilePath = p.ProfilePath })
                .ToListAsync(ct);

            return Results.Ok(new { titles, people });
        });

        // Live "Ask Reel": pulls matching titles from TMDB on the fly, embeds + personally scores
        // them, and STREAMS the experience (SSE: phase → candidate → candidate-scored → done) so
        // an engaging, live-updating UI appears instantly. Consumed via fetch + ReadableStream.
        group.MapPost("/ask", async (
            AskRequest request,
            HttpContext http,
            LiveSearchExpansionService expansion,
            IAccountContext accountContext,
            IReelDbContext db,
            CancellationToken ct) =>
        {
            if (accountContext.AccountId is not { } accountId)
            {
                return Results.Unauthorized();
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync(ct);

            var region = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Region).FirstOrDefaultAsync(ct);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

            string? assistantText = null;
            LiveSearchExpansionService.Emit emit = async (eventName, payload) =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions);
                if (eventName == "assistant-message")
                {
                    try { assistantText = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("text").GetString(); }
                    catch { /* non-fatal */ }
                }

                await http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            };

            var history = (request.History ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => new Domain.Ports.ChatTurn(t.Role ?? "user", t.Text!))
                .ToList();
            var shown = request.ShownTmdbIds ?? [];
            var message = request.Query ?? string.Empty;

            // Persist the turn so the conversation survives a reload (resume on the /search page).
            var now = DateTime.UtcNow;
            var conversation = request.ConversationId is { } cid
                ? await db.SearchConversations.FirstOrDefaultAsync(c => c.Id == cid && c.AccountId == accountId, ct)
                : null;
            if (conversation is null && message.Trim().Length > 0)
            {
                conversation = new Domain.Search.SearchConversation
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Title = message.Trim().Length > 80 ? message.Trim()[..80] : message.Trim(),
                    CreatedAt = now,
                    LastTurnAt = now,
                };
                db.SearchConversations.Add(conversation);
            }

            // A continuation replays a question that is already in the transcript, so it must not
            // be recorded again — only its results differ.
            if (conversation is not null)
            {
                conversation.LastTurnAt = now;
                if (!request.Continuation)
                {
                    db.SearchTurns.Add(new Domain.Search.SearchTurn
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        ConversationId = conversation.Id,
                        Role = "user",
                        Text = message,
                        CreatedAt = now,
                    });
                }

                await db.SaveChangesAsync(ct);
                await emit("conversation", new { id = conversation.Id });
            }

            try
            {
                var mediaFilter = (request.MediaTypes ?? [])
                    .Select(m => m?.Trim().ToLowerInvariant())
                    .Select(m => m is "movie" or "movies" or "film" ? MediaType.Movie
                        : m is "tv" or "show" or "series" ? MediaType.Show : (MediaType?)null)
                    .Where(m => m is not null)
                    .Select(m => m!.Value)
                    .Distinct()
                    .ToList();
                await expansion.StreamAsync(message, history, shown, region, mediaFilter, emit, ct);
            }
            catch (OperationCanceledException)
            {
                // client navigated away mid-stream — normal teardown
            }

            if (conversation is not null && !request.Continuation && assistantText is { Length: > 0 })
            {
                db.SearchTurns.Add(new Domain.Search.SearchTurn
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = conversation.Id,
                    Role = "assistant",
                    Text = assistantText,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }

            return Results.Empty;
        });

        // Resume: the most-recent conversation's transcript (newest convo, turns oldest→newest).
        group.MapGet("/conversations/latest", async (IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var convo = await db.SearchConversations
                .Where(c => c.AccountId == accountId)
                .OrderByDescending(c => c.LastTurnAt)
                .FirstOrDefaultAsync(ct);
            if (convo is null)
            {
                return Results.Ok(new { id = (Guid?)null, turns = Array.Empty<object>() });
            }

            var turns = await db.SearchTurns
                .Where(t => t.ConversationId == convo.Id)
                .OrderBy(t => t.CreatedAt)
                .Select(t => new { role = t.Role, text = t.Text })
                .ToListAsync(ct);
            return Results.Ok(new { id = convo.Id, title = convo.Title, turns });
        });
    }


    /// <summary>
    /// Trigram similarity between a title and a search term, mirroring how pg_trgm computes it:
    /// lowercase, split on non-alphanumerics, pad each word with two leading spaces and one
    /// trailing, then |shared trigrams| / |union|. Reimplemented in memory only because the SQL
    /// ordering is lost once the shortlist is re-sorted by predicted rating — the database remains
    /// the authority on which rows MATCH; this only re-establishes how well, for the tiebreak.
    /// </summary>
    public static double TitleSimilarity(string? candidate, string term)
    {
        var a = Trigrams(candidate);
        var b = Trigrams(term);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Intersect(b).Count();
        var union = a.Count + b.Count - shared;
        return union == 0 ? 0d : (double)shared / union;
    }

    private static HashSet<string> Trigrams(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        // Every non-alphanumeric is a SEPARATOR, not something to strip out of a word. That
        // distinction is the whole behaviour: pg_trgm reads "Spider-Man" as two words, which is why
        // the live database scores it 0.615 against "spiderman" rather than 1.0. Stripping the
        // hyphen instead would make this helper disagree with the query that selected the rows.
        foreach (var word in value.ToLowerInvariant().Split(
                     value.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                result.Add(padded.Substring(i, 3));
            }
        }

        return result;
    }

    public record AskRequest(
        string Query,
        List<AskTurn>? History,
        List<long>? ShownTmdbIds,
        Guid? ConversationId,
        List<string>? MediaTypes,
        // True when this request is a continuation of a question the user already asked — an
        // infinite-scroll page, or a re-ask triggered by toggling the media filter. The endpoint
        // used to have no way to tell those from a genuine new question, so it wrote a fresh
        // user+assistant SearchTurn pair for every scroll page and every filter click. The user
        // never saw that duplication live (the client hides it), but the persisted transcript
        // filled with repeats and the resumed conversation showed the same question over and over.
        bool Continuation = false);

    public record AskTurn(string? Role, string? Text);

}
