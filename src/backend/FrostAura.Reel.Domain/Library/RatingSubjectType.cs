namespace FrostAura.Reel.Domain.Library;

/// <summary>
/// What a Trakt rating was attached to. Movie/Show ratings are the model's training labels;
/// Season/Episode ratings are ingested but feed no model feature — the `showEngagement`
/// auxiliary they once fed was pinned to 0 in Phase 8b and deleted in FeatureSchema v4.
/// </summary>
public enum RatingSubjectType
{
    Movie,
    Show,
    Season,
    Episode,
}
