using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// HREV2-RET-02, the stored half: drops the <see cref="TitleScore"/> rows an Active artifact on an
/// older <see cref="TrainingRecipe"/> was scored with while serving did not describe the user the way
/// that artifact was trained.
///
/// <para><b>What was misserved.</b> From the show-rollup change (<see cref="MisservedSince"/>) until
/// serving followed the artifact's recipe in full, an artifact trained before the recipe stamp was
/// handed taste pools it never saw: rollups it was trained without (every 2020s title near 2 against
/// a 6.31 mean rating), and out-of-fold rows for the titles the user had rated (Boss Level, rated 7,
/// shown 2.0). Every read joins the Active artifact, so those rows kept being served until a retrain
/// replaced the artifact — and the retrain that does is behind the paid trust-purge switch, so a host
/// with it off (the eval and integration stacks) served them for good.</para>
///
/// <para><b>Why delete, and why once.</b> Nothing records which code scored a row, so the rows scored
/// since <see cref="MisservedSince"/> are dropped and re-scored, free, the next time a surface asks
/// (the title page, the paginator, the next build). The rows before it were scored by code that
/// served the artifact its own recipe and are the account's pre-rating history, which the closed loop
/// reads, so they stay. The artifact is then stamped (<see cref="Property"/>): rows scored after the
/// repair come from this build and must survive the next restart.</para>
///
/// <para>Unpaid and unswitched, like the feature-schema sweep: it only deletes rows and stamps the
/// artifact. Only Active artifacts are repaired; a superseded artifact's rows are never served and
/// age out with the retention purge.</para>
/// </summary>
public static class RecipeScoreRepair
{
    /// <summary>The <c>FeatureSchemaJson</c> property the repair stamps, with <see cref="Revision"/>.</summary>
    public const string Property = "scoresRepaired";

    /// <summary>The repair this build runs. An artifact stamped with it (or later) is left alone.</summary>
    public const int Revision = 1;

    /// <summary>
    /// When the first misserving build could have scored a row: the show-rollup commit (ba6b2f4,
    /// 2026-10-26 22:12:53 +02:00). Rows scored earlier came from code that served every artifact the
    /// pools it was trained on.
    /// </summary>
    public static readonly DateTime MisservedSince = new(2026, 10, 26, 20, 12, 53, DateTimeKind.Utc);

    /// <summary>One repaired artifact, for the log.</summary>
    public sealed record Repaired(Guid ArtifactId, Guid AccountId, int Version, string Recipe, int ScoresDropped);

    /// <summary>
    /// True when an Active artifact with this <c>FeatureSchemaJson</c> is owed the repair: trained on
    /// an older recipe and not yet stamped. An artifact on the current recipe was trained by code that
    /// serves it its own recipe, so its rows were never misserved.
    /// </summary>
    public static bool IsOwed(string? featureSchemaJson) =>
        !TrainingRecipe.IsCurrent(featureSchemaJson) && RevisionOf(featureSchemaJson) < Revision;

    /// <summary>The repair revision stamped into <paramref name="featureSchemaJson"/>, or 0.</summary>
    public static int RevisionOf(string? featureSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(featureSchemaJson))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(featureSchemaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(Property, out var revision)
                && revision.ValueKind == JsonValueKind.Number
                && revision.TryGetInt32(out var value)
                    ? value
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// <paramref name="featureSchemaJson"/> with <see cref="Revision"/> stamped in, every other
    /// property kept. A document that is not a JSON object is returned unchanged.
    /// </summary>
    public static string StampInto(string featureSchemaJson)
    {
        try
        {
            if (JsonNode.Parse(featureSchemaJson) is not JsonObject doc)
            {
                return featureSchemaJson;
            }

            doc[Property] = Revision;
            return doc.ToJsonString();
        }
        catch (JsonException)
        {
            return featureSchemaJson;
        }
    }

    /// <summary>
    /// Repairs every Active artifact that is owed it. The caller's context must see every tenant
    /// (system scope). Each artifact's rows are dropped and its stamp saved before the next one.
    /// </summary>
    public static async Task<IReadOnlyList<Repaired>> RunAsync(IReelDbContext db, CancellationToken ct)
    {
        var active = await db.ModelArtifacts
            .Where(a => a.Status == ArtifactStatus.Active)
            .ToListAsync(ct);

        var repaired = new List<Repaired>();
        foreach (var artifact in active.Where(a => IsOwed(a.FeatureSchemaJson)))
        {
            var artifactId = artifact.Id;
            var dropped = await db.TitleScores
                .Where(s => s.ModelArtifactId == artifactId && s.ScoredAt >= MisservedSince)
                .ExecuteDeleteAsync(ct);
            // Always an object here: the context's query filter only shows an artifact whose document
            // records the running feature-schema version.
            artifact.FeatureSchemaJson = StampInto(artifact.FeatureSchemaJson);
            await db.SaveChangesAsync(ct);
            repaired.Add(new Repaired(
                artifactId,
                artifact.AccountId,
                artifact.Version,
                TrainingRecipe.ReadFrom(artifact.FeatureSchemaJson) ?? "leave-one-out (unstamped)",
                dropped));
        }

        return repaired;
    }

    /// <summary>The log line for one repaired artifact.</summary>
    public static string Describe(Repaired repaired) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"model v{repaired.Version} ({repaired.Recipe}): dropped {repaired.ScoresDropped} score(s) written since {MisservedSince:O}");
}
