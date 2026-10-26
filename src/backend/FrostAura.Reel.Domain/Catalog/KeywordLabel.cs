using Pgvector;

namespace FrostAura.Reel.Domain.Catalog;

/// <summary>
/// One distinct TMDB keyword label in the catalogue, with its precomputed embedding — the row the
/// concept resolver's nearest-label fallback reads when a user's words match no label by containment
/// or synonym ("survival at sea" → "lost at sea", "castaway"). Global catalogue data, shared by every
/// tenant; written only by the vocabulary refresher.
/// </summary>
public class KeywordLabel
{
    /// <summary>The label exactly as <see cref="Title.Keywords"/> stores it (lower-cased by ingest).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Titles carrying the label when it was last counted.</summary>
    public int TitleCount { get; set; }

    /// <summary>The label's embedding, or null until the refresher has embedded it.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>The model <see cref="Embedding"/> came from; a label embedded by another model is re-embedded.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>When <see cref="TitleCount"/> was last refreshed.</summary>
    public DateTime CountedAt { get; set; }

    /// <summary>When <see cref="Embedding"/> was written, or null.</summary>
    public DateTime? EmbeddedAt { get; set; }
}
