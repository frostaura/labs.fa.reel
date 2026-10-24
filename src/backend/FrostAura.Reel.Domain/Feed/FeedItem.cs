using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

public enum FeedRowKind
{
    Hero,
    BecauseYouLoved,
    DeepCuts,
    NewForYou,
}

/// <summary>One ranked item inside a feed snapshot row. Continue-watching is served live, not snapshotted.</summary>
public class FeedItem : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid FeedSnapshotId { get; set; }

    public FeedRowKind Row { get; set; }

    /// <summary>The loved title seeding a BecauseYouLoved row.</summary>
    public Guid? AnchorTitleId { get; set; }

    public int Rank { get; set; }

    public Guid TitleId { get; set; }

    public decimal PredictedRating { get; set; }

    /// <summary>PredictedRating × freshness × diversity penalty — the actual sort key.</summary>
    public decimal FinalScore { get; set; }

    public string WhyThisSentence { get; set; } = string.Empty;

    /// <summary>Top contributions backing the why-this sentence (jsonb).</summary>
    public string ExplanationJson { get; set; } = "[]";

    /// <summary>
    /// How <see cref="WhyThisSentence"/> was produced — the exposure log's why-source name
    /// (<c>Llm</c>, <c>Cache</c>, <c>Templater</c>, <c>Preserved</c>, <c>Fallback</c>, <c>None</c>,
    /// …), read back through <c>ExposureWhySource.Parse</c>, which maps anything unrecognised to
    /// Unknown. Null on a row written before provenance was stored: the deploy purge reads that as
    /// "built under rules this image no longer trusts" and rebuilds the snapshot once.
    /// </summary>
    public string? WhySource { get; set; }

    /// <summary>
    /// On an anchored row: the shared evidence the anchored-row gate admitted this card on, as a
    /// jsonb array of <c>{kind, label, role}</c> — exactly what the card's chips render. Stored at
    /// deal time because the facts it was read from move (a theme re-extraction, a keyword refresh)
    /// and a kept card must keep showing the reason it was admitted for. Null off anchored rows and
    /// on rows written before it was stored.
    /// </summary>
    public string? EvidenceJson { get; set; }
}
