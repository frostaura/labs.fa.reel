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
/// titles that the features actually read (attribute extractions, embedding source text, the crowd
/// rating to one decimal and the vote count in log-tenths), the hyperparameters, the
/// feature-schema version and the training recipe (<see cref="TrainingRecipe"/>). A change here that
/// leaves the labels alone is catalogue drift. A metadata refresh TIMESTAMP is not an input — it
/// moved on every refresh whether or not anything the model reads changed.</item>
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
    /// <summary>
    /// The version of the fingerprint FORMULA. Stored beside the hashes; a stored pair from another
    /// formula reads as no pair at all (<see cref="ReadFrom"/>), so changing what is hashed is never
    /// mistaken for the inputs changing — which would retrain every account once on nothing. v2
    /// dropped the metadata-refresh timestamp and bucketed the crowd numbers.
    /// </summary>
    public const int FormulaVersion = 2;

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
            .Select(t => new { t.Id, t.TraktRating, t.TraktVotes })
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
            .Concat(metadata.Select(m => Line("m", m.Id, CrowdRatingBucket(m.TraktRating), VotesBucket(m.TraktVotes))))
            .Order(StringComparer.Ordinal)
            .ToList();

        var labelHash = Sha256(labelLines);
        var inputHash = Sha256(
            [
                Line("schema", FeatureSchema.Version),
                Line("recipe", TrainingRecipe.Current),
                Line("hyperparams", hyperparams),
                Line("labels", labelHash),
                .. catalogueLines,
            ]);
        return new Hashes(labelHash, inputHash);
    }

    /// <summary>
    /// The fingerprints an artifact was trained on, read from its <c>FeatureSchemaJson</c>
    /// document (where they are written — no column, no migration). Null for an artifact trained
    /// before they existed or under another <see cref="FormulaVersion"/>; the trainer then asks
    /// the timestamp question instead and restamps (TrainJobHandler.SkipHashlessAsync).
    /// </summary>
    public static Hashes? ReadFrom(string featureSchemaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(featureSchemaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("hashVersion", out var formula)
                && formula.ValueKind == JsonValueKind.Number && formula.TryGetInt32(out var v) && v == FormulaVersion
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

    /// <summary>
    /// The crowd rating at the resolution a retrain could act on: one decimal. Trakt's average
    /// moves in the second decimal with every few votes, and the feature is one input among ~60.
    /// </summary>
    public static decimal? CrowdRatingBucket(decimal? rating) =>
        rating is { } r ? Math.Round(r, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// The vote count as the feature reads it — <c>log1p(votes)</c> — bucketed to tenths: a change
    /// only registers once the count moves by roughly 10%. Raw counts tick on every Trakt ingest
    /// (IngestMapper rewrites them), which made every sync a "changed input" and a steady account
    /// retrain hourly; a retrain for 10 more votes on a film with 40,000 is noise.
    /// </summary>
    public static decimal VotesBucket(int votes) =>
        Math.Round((decimal)Math.Log(1d + Math.Max(0, votes)), 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <paramref name="featureSchemaJson"/> with the two fingerprints written in (replacing any
    /// already there), every other property kept as it was. A document that is not a JSON object is
    /// returned unchanged — the artifact then simply stays hashless.
    /// </summary>
    public static string StampInto(string featureSchemaJson, Hashes hashes)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(featureSchemaJson) is not System.Text.Json.Nodes.JsonObject doc)
            {
                return featureSchemaJson;
            }

            doc["hashVersion"] = FormulaVersion;
            doc["labelHash"] = hashes.Labels;
            doc["inputHash"] = hashes.Inputs;
            return doc.ToJsonString();
        }
        catch (JsonException)
        {
            return featureSchemaJson;
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
