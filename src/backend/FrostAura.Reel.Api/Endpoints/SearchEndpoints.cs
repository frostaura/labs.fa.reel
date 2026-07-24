using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
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
            // badged — but every exclusion still applies, airtight. Over-fetch by popularity, then
            // apply the in-memory maturity gate (certifications don't order in SQL) and keep 8, so a
            // restricted profile still lands a full shortlist instead of a starved one.
            var matched = await eligibility.ContentFilteredTitles(accountId)
                .Where(t => EF.Functions.ILike(t.Name, $"%{term}%"))
                .Where(t => !db.UserTitleReactions.Any(r =>
                    r.AccountId == accountId && r.TitleId == t.Id && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null))
                .OrderByDescending(t => t.TmdbPopularity)
                .Take(24)
                .ToListAsync(ct);

            var candidates = EligibilityQueryBuilder.WithinMaturity(matched, maturityCeiling).Take(8).ToList();
            var candidateIds = candidates.Select(t => t.Id).ToList();

            // Live per-user scoring of the ≤8 survivors against the Active artifact (cheap: one model
            // load + one taste build). No model yet ⇒ empty map ⇒ order falls back to popularity.
            var predicted = await scorer.ScoreAsync(accountId, candidateIds, DateTime.UtcNow, ct);
            var watchedFull = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && candidateIds.Contains(w.TitleId) && w.IsFullyWatched)
                .Select(w => w.TitleId)
                .ToListAsync(ct)).ToHashSet();

            var titles = candidates
                .OrderByDescending(t => predicted.TryGetValue(t.Id, out var p) ? p : decimal.MinValue)
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
                })
                .ToList();

            var people = await db.Persons
                .Where(p => EF.Functions.ILike(p.Name, $"%{term}%"))
                .Take(4)
                .Select(p => new { personId = p.Id, name = p.Name, knownFor = p.KnownForDepartment, profilePath = p.ProfilePath })
                .ToListAsync(ct);

            return Results.Ok(new { titles, people });
        });

        // Natural-language search: embedding-ranked when the OpenAI key is configured,
        // otherwise the typo-tolerant lexical engine — Ask Reel always answers.
        group.MapPost("/semantic", async (
            SemanticRequest request,
            IReelDbContext db,
            IAccountContext accountContext,
            EligibilityQueryBuilder eligibility,
            LexicalSearchService lexical,
            IEmbeddingProvider embeddings,
            OnDemandScorer scorer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest(new { error = "query is required" });
            }

            var accountId = accountContext.AccountId!.Value;
            var minPredicted = await MinPredictedFloorAsync(db, accountId, ct);
            var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);
            var now = DateTime.UtcNow;

            if (embeddings.IsAvailable && await db.TitleEmbeddings.AnyAsync(ct))
            {
                var vectors = await embeddings.EmbedAsync([request.Query.Trim()], ct);
                var queryVector = new Vector(vectors[0]);

                // Restrict the kNN to ELIGIBLE titles BEFORE ordering by cosine. The catalog is
                // dominated by the user's seen library, so a "top-N then filter to unseen" query
                // returns almost nothing — the unseen titles rarely crack the global top-N.
                var eligibleIds = eligibility.EligibleTitles(accountId).Select(t => t.Id);

                // Over-fetch to 32: the in-memory maturity gate trims below this before we keep 24, so
                // a restricted profile isn't starved. Pull the Title entity so the gate + live scorer
                // have everything they need without a second round-trip.
                var hits = await db.TitleEmbeddings
                    .Where(e => eligibleIds.Contains(e.TitleId))
                    .OrderBy(e => e.Embedding.CosineDistance(queryVector))
                    .Take(32)
                    .Join(db.Titles, e => e.TitleId, t => t.Id, (e, t) => new
                    {
                        Title = t,
                        similarity = 1 - e.Embedding.CosineDistance(queryVector),
                    })
                    .ToListAsync(ct);

                var safe = hits
                    .Where(h => EligibilityQueryBuilder.PassesMaturity(h.Title, maturityCeiling))
                    .Take(24)
                    .ToList();

                // Score the survivors LIVE against the Active artifact — no more stale TitleScores or
                // null→6.0 guessing; an unscored title (no model yet) still shows, just unranked.
                var predictedById = await scorer.ScoreAsync(accountId, safe.Select(h => h.Title.Id).ToList(), now, ct);

                var ranked = safe
                    .Select(h => new
                    {
                        titleId = h.Title.Id,
                        mediaType = h.Title.MediaType.ToString(),
                        tmdbId = h.Title.TmdbId,
                        name = h.Title.Name,
                        year = h.Title.Year,
                        posterPath = h.Title.PosterPath,
                        genres = h.Title.Genres,
                        similarity = h.similarity,
                        predictedRating = predictedById.TryGetValue(h.Title.Id, out var p) ? (decimal?)p : null,
                    })
                    .Where(h => h.predictedRating == null || h.predictedRating >= minPredicted)
                    .OrderByDescending(h => (h.similarity * 0.6) + ((double)(h.predictedRating ?? 6m) / 10d * 0.4))
                    .ToList();

                return Results.Ok(new { available = true, mode = "semantic", reason = (string?)null, results = (object)ranked });
            }

            // Lexical engine — concept/genre/keyword matching with typo tolerance (maturity gate
            // applied inside the service). Score the hits live, same as the embedding path.
            var lexicalHits = await lexical.SearchAsync(accountId, request.Query, 24, ct);
            var titleIds = lexicalHits.Select(h => h.Title.Id).ToList();
            var scores = await scorer.ScoreAsync(accountId, titleIds, now, ct);

            var maxScore = lexicalHits.Count > 0 ? lexicalHits.Max(h => h.MatchScore) : 1d;
            var results = lexicalHits
                .Select(h => new
                {
                    titleId = h.Title.Id,
                    mediaType = h.Title.MediaType.ToString(),
                    tmdbId = h.Title.TmdbId,
                    name = h.Title.Name,
                    year = h.Title.Year,
                    posterPath = h.Title.PosterPath,
                    genres = h.Title.Genres,
                    similarity = Math.Round(h.MatchScore / maxScore, 3),
                    matchedOn = h.MatchedOn,
                    predictedRating = scores.TryGetValue(h.Title.Id, out var predicted) ? (decimal?)predicted : null,
                })
                .Where(h => h.predictedRating == null || h.predictedRating >= minPredicted)
                .OrderByDescending(h => (h.similarity * 0.55) + ((double)(h.predictedRating ?? 6m) / 10d * 0.45))
                .ToList();

            return Results.Ok(new { available = true, mode = "lexical", reason = (string?)null, results = (object)results });
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

            if (conversation is not null)
            {
                conversation.LastTurnAt = now;
                db.SearchTurns.Add(new Domain.Search.SearchTurn
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = conversation.Id,
                    Role = "user",
                    Text = message,
                    CreatedAt = now,
                });
                await db.SaveChangesAsync(ct);
                await emit("conversation", new { id = conversation.Id });
            }

            try
            {
                await expansion.StreamAsync(message, history, shown, region, emit, ct);
            }
            catch (OperationCanceledException)
            {
                // client navigated away mid-stream — normal teardown
            }

            if (conversation is not null && assistantText is { Length: > 0 })
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

    public record SemanticRequest(string Query);

    public record AskTurn(string? Role, string? Text);

    public record AskRequest(string Query, List<AskTurn>? History, List<long>? ShownTmdbIds, Guid? ConversationId);

    /// <summary>The account's MinPredictedRating floor (0 = off) — recommendation surfaces only.</summary>
    public static async Task<decimal> MinPredictedFloorAsync(IReelDbContext db, Guid accountId, CancellationToken ct)
    {
        var raw = await db.ContentFilters
            .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinPredictedRating)
            .Select(f => f.Value)
            .FirstOrDefaultAsync(ct);
        return raw is not null && decimal.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 10m)
            : 0m;
    }
}
