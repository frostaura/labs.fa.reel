using System.Security.Cryptography;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// Content preferences (airtight across every surface via the EligibilityQueryBuilder) and
/// the optional settings PIN — the account creator keeps control on shared devices.
/// </summary>
public static class FilterEndpoints
{
    public record FiltersPayload(
        string[] ExcludeGenres, string[] IncludeGenres, string[] ExcludeKeywords, string? MaturityCeiling,
        decimal? MinPredictedRating, string[] Languages,
        decimal? MinCrowdRating = null, int? MinYear = null, int? MaxYear = null);

    public record PinRequest(string Pin, string? CurrentPin);

    public record PinVerifyRequest(string Pin);

    public static void MapFilterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").RequireAccount();

        group.MapGet("/filters", async (IReelDbContext db, IAccountContext accountContext, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            var filters = await db.ContentFilters.Where(f => f.AccountId == accountContext.AccountId!.Value).ToListAsync(ct);
            var minRaw = filters.FirstOrDefault(f => f.Kind == FilterKind.MinPredictedRating)?.Value;
            var languages = filters.Where(f => f.Kind == FilterKind.Language).Select(f => f.Value).ToArray();
            return Results.Ok(new FiltersPayload(
                filters.Where(f => f.Kind == FilterKind.ExcludeGenre).Select(f => f.Value).ToArray(),
                filters.Where(f => f.Kind == FilterKind.IncludeGenre).Select(f => f.Value).ToArray(),
                filters.Where(f => f.Kind == FilterKind.ExcludeKeyword).Select(f => f.Value).ToArray(),
                account?.Settings.MaturityCeiling,
                ContentFilterRules.ParseRating(minRaw),
                // No rows ⇒ the account is on the English-only default; surface that to the UI so the
                // English chip reads as selected rather than blank.
                languages.Length > 0 ? languages : ["en"],
                ContentFilterRules.ParseRating(filters.FirstOrDefault(f => f.Kind == FilterKind.MinCrowdRating)?.Value),
                ContentFilterRules.ParseYear(filters.FirstOrDefault(f => f.Kind == FilterKind.MinYear)?.Value),
                ContentFilterRules.ParseYear(filters.FirstOrDefault(f => f.Kind == FilterKind.MaxYear)?.Value)));
        });

        group.MapPut("/filters", async (
            FiltersPayload payload, IReelDbContext db, IAccountContext accountContext,
            FeedRefreshCoordinator feedRefresh, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (!await VerifyPinHeaderAsync(account.Settings.SettingsPinHash, http))
            {
                return Results.StatusCode(StatusCodes.Status423Locked);
            }

            // Validate BEFORE the destructive ExecuteDelete below — a rejected payload must leave the
            // account's existing filters exactly as they were, not wipe them and then 400.
            if (!ContentFilterRules.TryNormalizeYears(payload.MinYear, payload.MaxYear, out var years, out var yearError))
            {
                return Results.BadRequest(new { error = yearError });
            }

            var crowdFloor = ContentFilterRules.NormalizeCrowdFloor(payload.MinCrowdRating);

            var accountId = accountContext.AccountId!.Value;
            await db.ContentFilters.Where(f => f.AccountId == accountId).ExecuteDeleteAsync(ct);

            void AddAll(IEnumerable<string> values, FilterKind kind)
            {
                foreach (var value in values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct())
                {
                    db.ContentFilters.Add(new ContentFilter
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        Kind = kind,
                        Value = value,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
            }

            AddAll(payload.ExcludeGenres, FilterKind.ExcludeGenre);
            AddAll(payload.IncludeGenres, FilterKind.IncludeGenre);
            // Languages: persist the chosen allow-list. An empty selection is stored as no rows,
            // which the eligibility builder reads as the English-only default; "*" means all.
            AddAll(payload.Languages, FilterKind.Language);
            // Umbrella terms ("lgbtq") expand to their TMDB keyword family; each member
            // round-trips to the UI as its own removable chip.
            AddAll(KeywordFilterExpansion.Expand(payload.ExcludeKeywords), FilterKind.ExcludeKeyword);

            void AddSingle(FilterKind kind, string value) => db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = kind,
                Value = value,
                CreatedAt = DateTime.UtcNow,
            });

            if (ContentFilterRules.NormalizeCrowdFloor(payload.MinPredictedRating) is { } min)
            {
                AddSingle(FilterKind.MinPredictedRating, ContentFilterRules.FormatRating(min));
            }

            // The crowd-rating floor and the year window are single-valued, and the values are cast to
            // a number inside SQL by the eligibility builder — so they are written only through
            // ContentFilterRules, never hand-formatted.
            if (crowdFloor is { } crowd)
            {
                AddSingle(FilterKind.MinCrowdRating, ContentFilterRules.FormatRating(crowd));
            }

            if (years.MinYear is { } minYear)
            {
                AddSingle(FilterKind.MinYear, ContentFilterRules.FormatYear(minYear));
            }

            if (years.MaxYear is { } maxYear)
            {
                AddSingle(FilterKind.MaxYear, ContentFilterRules.FormatYear(maxYear));
            }

            account.Settings.MaturityCeiling = string.IsNullOrWhiteSpace(payload.MaturityCeiling) ? null : payload.MaturityCeiling;

            // Content rules just changed — the Active feed was built against the OLD filters, so a
            // newly-excluded genre / tightened maturity ceiling / raised crowd floor / narrowed year
            // window would linger until the next build. Enqueue a reactive rebuild (reuses the Active
            // model) so the feed re-filters immediately; it commits in the same SaveChanges as the
            // filter change. Kind-agnostic by construction — every filter, old and new, invalidates.
            await feedRefresh.RequestRebuildAsync(accountId, "content filters changed", ct);

            await db.SaveChangesAsync(ct);
            // Echo what was actually STORED, not what was sent: a swapped year window and a clamped
            // crowd floor must round-trip to the UI, or the editor keeps showing the rejected shape.
            return Results.Ok(payload with
            {
                MinPredictedRating = ContentFilterRules.NormalizeCrowdFloor(payload.MinPredictedRating),
                MinCrowdRating = crowdFloor,
                MinYear = years.MinYear,
                MaxYear = years.MaxYear,
            });
        });

        group.MapPost("/pin", async (PinRequest request, IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (request.Pin is not { Length: >= 4 and <= 8 } || !request.Pin.All(char.IsDigit))
            {
                return Results.BadRequest(new { error = "pin must be 4-8 digits" });
            }

            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (account.Settings.SettingsPinHash is not null
                && !VerifyPin(account.Settings.SettingsPinHash, request.CurrentPin ?? string.Empty))
            {
                return Results.StatusCode(StatusCodes.Status423Locked);
            }

            account.Settings.SettingsPinHash = HashPin(request.Pin);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { pinConfigured = true });
        });

        group.MapPost("/pin/verify", async (PinVerifyRequest request, IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            var valid = account?.Settings.SettingsPinHash is { } hash && VerifyPin(hash, request.Pin);
            return valid ? Results.Ok(new { valid = true }) : Results.StatusCode(StatusCodes.Status423Locked);
        });

        group.MapDelete("/pin", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (!await VerifyPinHeaderAsync(account.Settings.SettingsPinHash, http))
            {
                return Results.StatusCode(StatusCodes.Status423Locked);
            }

            account.Settings.SettingsPinHash = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { pinConfigured = false });
        });
    }

    private static Task<bool> VerifyPinHeaderAsync(string? pinHash, HttpContext http)
    {
        if (pinHash is null)
        {
            return Task.FromResult(true); // no PIN configured — open
        }

        var supplied = http.Request.Headers["X-Settings-Pin"].FirstOrDefault() ?? string.Empty;
        return Task.FromResult(VerifyPin(pinHash, supplied));
    }

    internal static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }

    internal static bool VerifyPin(string stored, string pin)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromHexString(parts[0]);
        var expected = Convert.FromHexString(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
