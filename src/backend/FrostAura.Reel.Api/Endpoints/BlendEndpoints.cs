using FrostAura.Reel.Application.Blend;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Api.Endpoints;

public static class BlendEndpoints
{
    /// <summary>
    /// A joint pick: the shared card fields plus both personal predictions. <c>Joint</c> is
    /// min(<c>ForYou</c>, <c>ForThem</c>) — the ordering key. All three rounded to 1dp at
    /// this edge, like every other rating the API serves.
    /// </summary>
    public record BlendCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal? CrowdRating, int? CrowdVotes, bool IsReleased,
        IReadOnlyList<FeedEndpoints.CardProvider> Providers,
        decimal ForYou, decimal ForThem, decimal Joint);

    private const int PickCount = 20;

    public static void MapBlendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/blend").RequireAccount();

        group.MapPost("/code", async (IAccountContext accountContext, BlendService blend, CancellationToken ct) =>
        {
            var code = await blend.RegenerateCodeAsync(accountContext.AccountId!.Value, ct);
            return Results.Ok(new { code });
        });

        group.MapGet("/{code}/preview", async (
            string code, IAccountContext accountContext, BlendService blend, CancellationToken ct) =>
        {
            var owner = await blend.FindOwnerByCodeAsync(code, ct);
            if (owner is null)
            {
                return Results.NotFound();
            }

            var count = await blend.EligibleBothCountAsync(accountContext.AccountId!.Value, owner.Id, ct);
            return Results.Ok(new { ownerName = owner.DisplayName, eligibleBothCount = count });
        });

        group.MapPost("/{code}/picks", async (
            string code, IReelDbContext db, BlendService blend, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var owner = await blend.FindOwnerByCodeAsync(code, ct);
            if (owner is null)
            {
                return Results.NotFound();
            }

            var picks = await blend.JointPicksAsync(account.Id, owner.Id, PickCount, ct);
            var nowUtc = DateTime.UtcNow;
            var providers = await FeedEndpoints.ProvidersForAsync(
                db, account.Region, picks.Select(p => p.Title.Id).ToList(), nowUtc, ct);

            var cards = picks.Select(pick =>
            {
                var title = pick.Title;
                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = CrowdRating.Resolve(title);
                return new BlendCard(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    crowd, crowdVotes, isReleased,
                    providers.GetValueOrDefault(title.Id, []),
                    Math.Round(pick.ForYou, 1), Math.Round(pick.ForThem, 1), Math.Round(pick.Joint, 1));
            }).ToList();

            return Results.Ok(new { ownerName = owner.DisplayName, items = cards });
        });
    }
}
