using FrostAura.Reel.Application.Persistence;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Stores an enum as its member name — exactly what EF's built-in string conversion writes, so the
/// change is invisible to every existing row, index filter and raw-SQL literal — and reads a name
/// this build does not define as <see cref="PersistedEnumFallbacks"/>' fallback for the enum
/// instead of throwing.
///
/// <para>Why: the program rolls images back by tag after additive migrations (§5.0 rule 8). A newer
/// image may already have written a member this one has never heard of (<c>NotEnoughRatings</c>
/// was the first), and EF's default converter throws on it — which fails every query that touches
/// such a row, the status poll included. Only an exact member name reads as that member: a stored
/// "3", " Movie", "movie" or "Movie, Show" is not a name any build wrote, so it is unknown too.</para>
/// </summary>
public sealed class TolerantEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public TolerantEnumConverter()
        : base(value => value.ToString(), stored => Read(stored))
    {
    }

    /// <summary>The value a stored name reads as. Public so the fallback rule is testable without a database.</summary>
    public static TEnum Read(string stored) =>
        TolerantJsonEnum.Named<TEnum>(stored) ?? PersistedEnumFallbacks.For<TEnum>();
}
