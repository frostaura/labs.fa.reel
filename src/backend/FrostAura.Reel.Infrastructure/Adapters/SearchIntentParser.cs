using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Parses an LLM discovery-spec JSON object into a <see cref="SearchIntent"/> — shared by the
/// query interpreter and the conversational agent so the contract (allowed genre vocabulary,
/// media-type mapping, keyword fallback) is single-sourced.
/// </summary>
internal static class SearchIntentParser
{
    public static SearchIntent FromJson(JsonElement root, string fallbackText, SearchIntent heuristicFallback)
    {
        string[] StrArray(string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().Take(8).ToArray()
                : [];

        var excludeGenres = StrArray("excludeGenres").Where(TmdbGenres.SlugToIds.ContainsKey).ToArray();
        var excludeKeywords = StrArray("excludeKeywords");

        // A term the model listed on both sides is a negation it half-applied ("less horror" →
        // genres:[horror], excludeGenres:[horror]). Negation wins: acting on the positive would
        // fetch exactly what the user rejected, which is the louder failure.
        var genres = StrArray("genres").Where(TmdbGenres.SlugToIds.ContainsKey).Except(excludeGenres).ToArray();
        var keywords = StrArray("keywords").Except(excludeKeywords).ToArray();
        var media = StrArray("mediaTypes")
            .Select(m => m.StartsWith("movie") ? MediaType.Movie : m is "tv" or "show" or "series" ? MediaType.Show : (MediaType?)null)
            .Where(m => m is not null).Select(m => m!.Value).Distinct().ToArray();
        int? minYear = root.TryGetProperty("minYear", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yr) && yr > 1870
            ? yr : null;
        int? maxYear = root.TryGetProperty("maxYear", out var my) && my.ValueKind == JsonValueKind.Number && my.TryGetInt32(out var myr) && myr > 1870
            ? myr : null;

        // A model that inverts the bounds ("80s" → min 1989, max 1980) would otherwise produce a
        // window that can never match. Swapping is strictly better than dropping the constraint.
        if (minYear is { } lo && maxYear is { } hi && lo > hi)
        {
            (minYear, maxYear) = (hi, lo);
        }
        var freeText = root.TryGetProperty("freeText", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } s
            ? s : fallbackText;
        var people = ParsePeople(root);
        var referenceTitles = ParseReferenceTitles(root);

        // Sanity bounds: a runtime cap must be a plausible number of minutes. Anything outside
        // (0, 1000] is a model misfire (seconds, or a year) and is safer dropped than applied.
        int? maxRuntime = root.TryGetProperty("maxRuntimeMinutes", out var rt) && rt.ValueKind == JsonValueKind.Number
            && rt.TryGetInt32(out var minutes) && minutes is > 0 and <= 1000
            ? minutes : null;

        // A model that returned nothing usable still gets the heuristic's keywords so discovery
        // never fires empty — but a named person IS the discovery signal, so it shortcuts that,
        // and so does a reference title ("more like Blade Runner" carries no genre or keyword).
        if (genres.Length == 0 && keywords.Length == 0 && people.Count == 0 && referenceTitles.Count == 0)
        {
            return heuristicFallback with
            {
                MediaTypes = media.Length > 0 ? media : heuristicFallback.MediaTypes,
                MinYear = minYear ?? heuristicFallback.MinYear,
                MaxYear = maxYear ?? heuristicFallback.MaxYear,
                // Negations survive a positive-signal fallback: "no ghosts" still means no ghosts
                // even when the rest of the spec came from the heuristic. The runtime cap is a
                // constraint of the same kind and survives the same way.
                ExcludeGenres = excludeGenres.Length > 0 ? excludeGenres : heuristicFallback.ExcludeGenres,
                ExcludeKeywords = excludeKeywords.Length > 0 ? excludeKeywords : heuristicFallback.ExcludeKeywords,
                MaxRuntimeMinutes = maxRuntime ?? heuristicFallback.MaxRuntimeMinutes,
            };
        }

        return new SearchIntent(
            genres, keywords, media, freeText, minYear, maxYear, people, excludeGenres, excludeKeywords,
            referenceTitles, maxRuntime);
    }

    /// <summary>
    /// Parses "referenceTitles":["Blade Runner", …] — comparison titles, NOT search keywords.
    /// Case is preserved (unlike <c>StrArray</c>): these names are resolved against the catalogue
    /// and TMDB title search and echoed into the rerank prompt, where "blade runner" reads wrong.
    /// </summary>
    private static IReadOnlyList<string> ParseReferenceTitles(JsonElement root)
    {
        if (!root.TryGetProperty("referenceTitles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    /// <summary>Parses a "people":[{"name","role"}] array — role ∈ director|cast|any (default any).</summary>
    private static IReadOnlyList<PersonQuery> ParsePeople(JsonElement root)
    {
        if (!root.TryGetProperty("people", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PersonQuery>();
        foreach (var el in arr.EnumerateArray().Take(3))
        {
            var name = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var roleStr = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim().ToLowerInvariant() : null;
            var role = roleStr switch
            {
                "director" or "directed" or "directing" => PersonRole.Director,
                "cast" or "actor" or "actress" or "star" or "starring" => PersonRole.Cast,
                _ => PersonRole.Any,
            };
            result.Add(new PersonQuery(name.Trim(), role));
        }

        return result;
    }
}
