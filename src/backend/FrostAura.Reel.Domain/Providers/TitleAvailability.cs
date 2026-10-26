namespace FrostAura.Reel.Domain.Providers;

/// <summary>
/// Region-scoped watch-provider snapshot for one title — shared cache of TMDB
/// watch/providers (JustWatch-powered), refreshed when older than 24h, on demand.
/// </summary>
public class TitleAvailability
{
    public Guid Id { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>ISO-3166 alpha-2.</summary>
    public string Region { get; set; } = string.Empty;

    public Guid ProviderId { get; set; }

    public AvailabilityKind Kind { get; set; }

    /// <summary>
    /// When this fetch was made. A cache timestamp and nothing more: every refetch rewrites it, so it
    /// says nothing about when the title arrived on the provider (IR2S-02 — the digest read it as an
    /// arrival and told the founder NCIS had "just landed" on Netflix).
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// When Reel first saw this (title, region, provider, kind) offer appear in a snapshot that had
    /// been fetched before — i.e. the moment the provider set changed. Carried unchanged across
    /// every refetch that still holds the offer, so a cache refresh can never make it new again.
    /// <para>
    /// Null means "no arrival observed": the offer was already there on Reel's first look at this
    /// title in this region, or it predates the column. Null is the honest default for both — Reel
    /// cannot know when an offer it has always seen began, so it never calls one new. Nullable, with
    /// no backfill, so the column is additive and an older image simply ignores it.
    /// </para>
    /// <para>Written only by <c>AvailabilitySnapshot.ReplaceAsync</c>.</para>
    /// </summary>
    public DateTime? FirstSeenAt { get; set; }
}
