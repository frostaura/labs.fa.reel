using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// A fingerprint of everything a retrain would learn from, so a retrain whose inputs did not change
/// can be skipped instead of promoted. Two halves, because they answer different questions:
/// <list type="bullet">
/// <item><see cref="Hashes.Labels"/> — the user's own evidence: the movie/show labels, explicit
/// person ratings and live reactions (the dislike features read them). A change here is the user
/// telling the model something new.</item>
/// <item><see cref="Hashes.Inputs"/> — the labels plus what the catalogue says about the rated
/// titles (attribute extractions, embedding source text, metadata refreshes), the hyperparameters
/// and the feature-schema version. A change here that leaves the labels alone is catalogue drift.</item>
/// </list>
///
/// <para><b>Why raw inputs and not the feature matrix.</b> Several features are functions of the
/// clock (release age, recency-weighted genre drift), so the matrix itself differs on every run and
/// a hash of it would never match. The rows the matrix is computed FROM do not move unless something
/// real changed. <c>RatedAt</c> is deliberately absent: every Trakt ratings walk rewrites it with no
/// change to the rating, and that is not a reason to retrain — <c>FirstRatedAt</c> is the immutable
/// axis and is in the hash.</para>
///
/// <para>Measured before this existed: 4 retrains in 8 minutes with 0 new ratings, each one
/// promoted and each one re-dealing the feed and re-spending the cross-reference pass.</para>
/// </summary>
public static class TrainingInputHash
{
    /// <summary>The two fingerprints, as lower-case hex SHA-256.</summary>
    public readonly record struct Hashes(string Labels, string Inputs);

    /// <summary>Computes both fingerprints for an account's next training run.</summary>
    public static async Task<Hashes> ComputeAsync(IReelDbContext db, Guid accountId, string hyperparams, CancellationToken ct)
    {
        var labels = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating, r.FirstRatedAt })
            .ToListAsync(ct);
        var people = await db.UserPersonRatings
            .Where(r => r.AccountId == accountId)
            .Select(r => new { r.PersonId, r.Rating })
            .ToListAsync(ct);
        var reactions = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.RevokedAt == null)
            .Select(r => new { r.TitleId, r.Kind, r.Reason })
            .ToListAsync(ct);

        var titleIds = labels.Select(l => l.TitleId).Distinct().ToList();
        var attributes = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId))
            .Select(a => new { a.TitleId, a.Status, a.ExtractorVersion, a.ExtractedAt })
            .ToListAsync(ct);
        var embeddings = await db.TitleEmbeddings
            .Where(e => titleIds.Contains(e.TitleId))
            .Select(e => new { e.TitleId, e.SourceTextHash })
            .ToListAsync(ct);
        var metadata = await db.Titles
            .Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.LastMetadataRefreshAt, t.TraktRating, t.TraktVotes })
            .ToListAsync(ct);

        var labelLines = labels
            .Select(l => Line("r", l.TitleId, l.Rating, l.FirstRatedAt))
            .Concat(people.Select(p => Line("p", p.PersonId, p.Rating)))
            .Concat(reactions.Select(r => Line("x", r.TitleId, r.Kind, r.Reason)))
            .Order(StringComparer.Ordinal)
            .ToList();

        var catalogueLines = attributes
            .Select(a => Line("a", a.TitleId, a.Status, a.ExtractorVersion, a.ExtractedAt))
            .Concat(embeddings.Select(e => Line("e", e.TitleId, e.SourceTextHash)))
            .Concat(metadata.Select(m => Line("m", m.Id, m.LastMetadataRefreshAt, m.TraktRating, m.TraktVotes)))
            .Order(StringComparer.Ordinal)
            .ToList();

        var labelHash = Sha256(labelLines);
        var inputHash = Sha256(
            [
                Line("schema", FeatureSchema.Version),
                Line("hyperparams", hyperparams),
                Line("labels", labelHash),
                .. catalogueLines,
            ]);
        return new Hashes(labelHash, inputHash);
    }

    /// <summary>
    /// The fingerprints an artifact was trained on, read from its <c>FeatureSchemaJson</c>
    /// document (where they are written — no column, no migration). Null for an artifact trained
    /// before they existed, which therefore never matches and always permits the next retrain.
    /// </summary>
    public static Hashes? ReadFrom(string featureSchemaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(featureSchemaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("labelHash", out var labels)
                && doc.RootElement.TryGetProperty("inputHash", out var inputs)
                && labels.GetString() is { Length: > 0 } l
                && inputs.GetString() is { Length: > 0 } i
                    ? new Hashes(l, i)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Line(string kind, params object?[] parts) =>
        kind + "|" + string.Join("|", parts.Select(p => p switch
        {
            null => "~",
            DateTime d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));

    private static string Sha256(IEnumerable<string> lines)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(line));
            sha.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
