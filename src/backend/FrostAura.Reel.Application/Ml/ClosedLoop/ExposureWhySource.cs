using System.Diagnostics.CodeAnalysis;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// The closed set of ways a why-line can come to be on a served card. <see cref="Unknown"/> is
/// zero on purpose: a card nobody attributed is recorded as unattributed, never guessed at.
/// Switch on this; persist <see cref="ExposureWhySource.Name"/>.
/// </summary>
public enum WhySourceKind
{
    /// <summary>The surface did not say. Recorded as such; the trust acceptance counts these to zero.</summary>
    Unknown = 0,

    /// <summary>A model pass wrote this sentence for this card, in this serve.</summary>
    Llm,

    /// <summary>Model text served from the per-(account, title, row) fit cache.</summary>
    Cache,

    /// <summary>The deterministic templater wrote it from verified contributions.</summary>
    Templater,

    /// <summary>Carried over unchanged from the previous snapshot's row.</summary>
    Preserved,

    /// <summary>Legacy: the single generic floor sentence, or an empty caption. New writers use <see cref="None"/> for an empty caption.</summary>
    Generic,

    /// <summary>The digest's own fixed reason copy.</summary>
    Digest,

    /// <summary>
    /// Deterministic catalogue evidence (keywords, credits, facets) produced the sentence — WS-1's
    /// <c>ResultSource.Facet</c>.
    /// </summary>
    Facet,

    /// <summary>
    /// A model why pass was attempted for this card and produced nothing usable — failed,
    /// truncated, timed out or rejected by the voice guard (WS-1's <c>ResultSource.Fallback</c>).
    /// Whatever sentence the card carries is deterministic copy, never model text.
    /// </summary>
    Fallback,

    /// <summary>No why-line was rendered and none was attempted.</summary>
    None,
}

/// <summary>
/// The typed why-source every exposure writer hands to <see cref="ExposureLog"/>. It replaces the
/// former string constants of the same name, and every <c>ExposureWhySource.X</c> member those
/// callers named still exists with the same stored value.
///
/// <para><b>Why a struct and not a bare enum.</b> The source must be typed going IN to the log —
/// <see cref="ExposureCard.WhySource"/> accepts nothing else, and there is deliberately no
/// conversion from <see cref="string"/>, so a source can never again be inferred from "the string
/// is non-empty". Going OUT it still has to land in string-typed places that other lanes own (the
/// Ask turn-card column, the legacy turn-card record), so it converts implicitly to its stored
/// name. One direction is typed, the other is compatible, and neither can be got wrong silently.</para>
///
/// <para><b>Persistence.</b> <c>ExposureEvents.WhySource</c> stays <c>varchar(32)</c> holding
/// <see cref="Name"/>. The names are spelled out literally below rather than taken from
/// <see cref="Enum.ToString()"/>, so renaming an enum member can never rename what the
/// append-only log has already written. Readers go through <see cref="Parse"/>, which maps every
/// value it does not recognise — a newer image's addition, an empty legacy row — to
/// <see cref="Unknown"/>, so an older image can always read a newer image's rows.</para>
/// </summary>
public readonly struct ExposureWhySource : IEquatable<ExposureWhySource>
{
    /// <inheritdoc cref="WhySourceKind.Unknown"/>
    public static readonly ExposureWhySource Unknown = new(WhySourceKind.Unknown);

    /// <inheritdoc cref="WhySourceKind.Llm"/>
    public static readonly ExposureWhySource Llm = new(WhySourceKind.Llm);

    /// <inheritdoc cref="WhySourceKind.Cache"/>
    public static readonly ExposureWhySource Cache = new(WhySourceKind.Cache);

    /// <inheritdoc cref="WhySourceKind.Templater"/>
    public static readonly ExposureWhySource Templater = new(WhySourceKind.Templater);

    /// <inheritdoc cref="WhySourceKind.Preserved"/>
    public static readonly ExposureWhySource Preserved = new(WhySourceKind.Preserved);

    /// <inheritdoc cref="WhySourceKind.Generic"/>
    public static readonly ExposureWhySource Generic = new(WhySourceKind.Generic);

    /// <inheritdoc cref="WhySourceKind.Digest"/>
    public static readonly ExposureWhySource Digest = new(WhySourceKind.Digest);

    /// <inheritdoc cref="WhySourceKind.Facet"/>
    public static readonly ExposureWhySource Facet = new(WhySourceKind.Facet);

    /// <inheritdoc cref="WhySourceKind.Fallback"/>
    public static readonly ExposureWhySource Fallback = new(WhySourceKind.Fallback);

    /// <inheritdoc cref="WhySourceKind.None"/>
    public static readonly ExposureWhySource None = new(WhySourceKind.None);

    /// <summary>Every defined source, in declaration order.</summary>
    public static IReadOnlyList<ExposureWhySource> All { get; } =
        [Unknown, Llm, Cache, Templater, Preserved, Generic, Digest, Facet, Fallback, None];

    private ExposureWhySource(WhySourceKind kind) => Kind = kind;

    /// <summary>The source. <c>default(ExposureWhySource)</c> is <see cref="WhySourceKind.Unknown"/>.</summary>
    public WhySourceKind Kind { get; }

    /// <summary>The persisted value. Never changes for a given <see cref="Kind"/>.</summary>
    public string Name => Kind switch
    {
        WhySourceKind.Llm => "Llm",
        WhySourceKind.Cache => "Cache",
        WhySourceKind.Templater => "Templater",
        WhySourceKind.Preserved => "Preserved",
        WhySourceKind.Generic => "Generic",
        WhySourceKind.Digest => "Digest",
        WhySourceKind.Facet => "Facet",
        WhySourceKind.Fallback => "Fallback",
        WhySourceKind.None => "None",
        _ => "Unknown",
    };

    /// <summary>
    /// The typed source for <paramref name="kind"/>; an undefined numeric value maps to
    /// <see cref="Unknown"/> rather than leaking a number into the log.
    /// </summary>
    public static ExposureWhySource From(WhySourceKind kind) =>
        Enum.IsDefined(kind) ? new ExposureWhySource(kind) : Unknown;

    /// <summary>
    /// The exposure source for a value WS-1's agent reported producing: <c>Llm</c> → <see cref="Llm"/>,
    /// <c>Facet</c> → <see cref="Facet"/>, and <c>Fallback</c> — or any value a later build adds —
    /// → <see cref="Fallback"/>. Never <see cref="Llm"/> for anything the agent did not mark as its
    /// own: an unknown provenance is untrusted provenance.
    /// </summary>
    public static ExposureWhySource From(Domain.Ports.ResultSource source) => source switch
    {
        Domain.Ports.ResultSource.Llm => Llm,
        Domain.Ports.ResultSource.Facet => Facet,
        _ => Fallback,
    };

    /// <summary>
    /// Reads a stored value back. Case-insensitive; null, empty, whitespace and every unrecognised
    /// value map to <see cref="Unknown"/> — never an exception, because the log outlives the code
    /// that wrote it.
    /// </summary>
    public static ExposureWhySource Parse(string? stored) =>
        TryParse(stored, out var source) ? source : Unknown;

    /// <summary>True only when <paramref name="stored"/> is a recognised name; otherwise <see cref="Unknown"/>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? stored, out ExposureWhySource source)
    {
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var trimmed = stored.Trim();
            foreach (var candidate in All)
            {
                if (string.Equals(candidate.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    source = candidate;
                    return true;
                }
            }
        }

        source = Unknown;
        return false;
    }

    /// <summary>
    /// Out to string-typed columns and records only. There is no conversion the other way, by design.
    /// </summary>
    public static implicit operator string(ExposureWhySource source) => source.Name;

    public static bool operator ==(ExposureWhySource left, ExposureWhySource right) => left.Equals(right);

    public static bool operator !=(ExposureWhySource left, ExposureWhySource right) => !left.Equals(right);

    public bool Equals(ExposureWhySource other) => Kind == other.Kind;

    public override bool Equals(object? obj) => obj is ExposureWhySource other && Equals(other);

    public override int GetHashCode() => (int)Kind;

    public override string ToString() => Name;
}
