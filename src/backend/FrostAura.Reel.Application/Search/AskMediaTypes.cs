using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The <c>mediaTypes</c> request filter: the closed token vocabulary, and the refusal for anything
/// outside it.
///
/// <para><b>Why a token the server cannot honour is a refusal rather than a shrug.</b> The tokens
/// used to be mapped inline with a <c>Where(m =&gt; m is not null)</c> after the switch, so
/// <c>["cartoon"]</c>, <c>[""]</c> and <c>[null]</c> all collapsed to "no filter" in silence. The
/// caller got a full, billed turn back — twenty-four picks across both media types — with nothing
/// anywhere on the wire saying its filter had been discarded, which makes a typo'd token and a
/// stale one indistinguishable from an intentionally unfiltered ask. The contract enumerates six
/// tokens; a seventh is a request this server cannot answer, and the honest moment to say so is
/// before the money is spent.</para>
///
/// <para>Its own type rather than a private helper on the endpoint because the vocabulary is the
/// contract's, and the resume payload already translates the other way
/// (<c>Show</c> → <c>tv</c>) — two halves of one table that must not drift.</para>
/// </summary>
public static class AskMediaTypes
{
    /// <summary>Every token the request accepts, as documented. Ordinal-insensitive, trimmed.</summary>
    public static readonly IReadOnlyList<string> Tokens = ["movie", "movies", "film", "tv", "show", "series"];

    /// <summary>
    /// The distinct media types a request's tokens resolve to, or <c>null</c> when ANY token is
    /// outside <see cref="Tokens"/>.
    ///
    /// <para>Null is the failure signal rather than an exception because the caller's answer is a
    /// 400 in the house shape, not a 500 — and rather than an empty list because an empty list is
    /// the legitimate "no filter" case that a missing or empty <c>mediaTypes</c> array produces.</para>
    /// </summary>
    public static IReadOnlyList<MediaType>? Resolve(IReadOnlyList<string?>? tokens)
    {
        if (tokens is null || tokens.Count == 0)
        {
            return [];
        }

        var resolved = new List<MediaType>(2);
        foreach (var token in tokens)
        {
            if (Map(token) is not { } mediaType)
            {
                return null;
            }

            if (!resolved.Contains(mediaType))
            {
                resolved.Add(mediaType);
            }
        }

        return resolved;
    }

    /// <summary>
    /// The prose for the refusal, naming the FIRST offending token and the whole vocabulary. The
    /// token is quoted back because a client that sent it cannot fix what it is not told, and the
    /// list is spelled out because the only other statement of it is a doc the client cannot read.
    /// </summary>
    public static string Refusal(IReadOnlyList<string?>? tokens)
    {
        var offender = (tokens ?? []).FirstOrDefault(t => Map(t) is null);
        var vocabulary = string.Join(", ", Tokens);
        return string.IsNullOrWhiteSpace(offender)
            ? $"I don't know an empty filter. Use one of: {vocabulary}."
            : $"I don't know the filter \"{offender.Trim()}\". Use one of: {vocabulary}.";
    }

    /// <summary>
    /// One token to a <see cref="MediaType"/>, or null when it is not in the vocabulary. Lowercased
    /// with the INVARIANT culture, like every other comparison this backend makes: a Turkish-locale
    /// host lowercases "TV" to "tv" with a dotless ı and the token would stop matching.
    /// </summary>
    private static MediaType? Map(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" => MediaType.Movie,
        "tv" or "show" or "series" => MediaType.Show,
        _ => null,
    };
}
