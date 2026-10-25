using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// The track record, as an API. <b>Free for every tier</b>, deliberately: Taste DNA is free on the
/// same reasoning, and a paid accuracy report reads as hiding the scorecard.
///
/// <para>Rounding happens here and nowhere else — <see cref="AccuracyQuery"/> keeps full precision
/// so every number can be re-derived, and the page gets the digits a human can read. Nothing is
/// softened on the way out: a near-zero correlation and a negative counterfactual render exactly as
/// they are, and <c>source</c> says which evidence base produced them.</para>
/// </summary>
public static class AccuracyEndpoints
{
    public static void MapAccuracyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accuracy").RequireAccount();

        group.MapGet("/", async (
            string? window, AccuracyQuery accuracy, CancellationToken ct) =>
        {
            var requested = string.IsNullOrWhiteSpace(window) ? "90d" : window;
            if (!AccuracyQuery.Windows.ContainsKey(requested))
            {
                // An unknown window is a 400, never a silent fallback to the default: a client
                // asking for "1y" and quietly getting 90 days would draw a chart labelled with a
                // period it does not cover.
                return Results.BadRequest(new
                {
                    error = $"window must be one of: {string.Join(", ", AccuracyQuery.Windows.Keys)}",
                });
            }

            var report = await accuracy.ComputeAsync(requested, DateTime.UtcNow, ct);

            return Results.Ok(new
            {
                window = report.Window,
                from = report.From,
                to = report.To,
                source = report.Source,
                headline = new
                {
                    withinOnePoint = Round(report.Headline.WithinOnePointRate, 4),
                    n = report.Headline.Count,
                    meanAbsoluteError = Round(report.Headline.MeanAbsoluteError, 2),
                    bias = Round(report.Headline.Bias, 2),
                    correlation = Round(report.Headline.PearsonCorrelation, 4),
                    meanPredicted = Round(report.Headline.MeanPredicted, 2),
                    meanActual = Round(report.Headline.MeanActual, 2),
                },
                calibration = report.Calibration.Select(b => new
                {
                    bucket = b.Index,
                    low = b.Low,
                    high = b.High,
                    n = b.Count,
                    meanPredicted = Round(b.MeanPredicted, 2),
                    meanActual = Round(b.MeanActual, 2),
                }),
                moments = report.Moments.Select(m => new
                {
                    titleId = m.TitleId,
                    name = m.Name,
                    year = m.Year,
                    posterPath = m.PosterPath,
                    mediaType = m.MediaType,
                    tmdbId = m.TmdbId,
                    predicted = Round(m.Predicted, 2),
                    actual = m.Actual,
                    surface = m.Surface,
                    shownAt = m.ShownAt,
                    ratedAt = m.RatedAt,
                    daysToOutcome = m.DaysToOutcome,
                }),
                conversion = new
                {
                    shown = report.Conversion.Shown,
                    saved = report.Conversion.Saved,
                    watched = report.Conversion.Watched,
                    rated = report.Conversion.Rated,
                    shownToSaved = Round(report.Conversion.ShownToSaved, 4),
                    savedToWatched = Round(report.Conversion.SavedToWatched, 4),
                    shownToRated = Round(report.Conversion.ShownToRated, 4),
                    medianDaysToWatch = Round(report.Conversion.MedianDaysToWatch, 1),
                },
                counterfactual = report.Counterfactual.Select(w => new
                {
                    window = w.Window,
                    days = w.Days,
                    viaReelCount = w.ViaReelCount,
                    viaReelMeanRating = Round(w.ViaReelMeanRating, 2),
                    otherCount = w.OtherCount,
                    otherMeanRating = Round(w.OtherMeanRating, 2),
                    delta = Round(w.Delta, 2),
                }),
                // Null when there is nothing honest to say — no predecessor artifact, or no
                // evaluation yet. A band reading "learned from 0 new ratings" is worse than none.
                learnedThisWeek = report.LearnedThisWeek is { } l
                    ? new
                    {
                        trainedAt = l.TrainedAt,
                        newRatings = l.NewRatingsSinceLast,
                        calibrationBias = l.CalibrationBias,
                        calibrationMae = l.CalibrationMae,
                        spearman = l.Spearman,
                        picksChanged = l.PicksChanged,
                    }
                    : null,
                // The cross-reference block: what the offline backtest measured, and what the cards
                // actually served under each standing went on to be rated. The two halves are kept
                // apart on purpose — the backtest is the gate, the online half is corroboration
                // that cannot reach significance and says so in its own `answerable` flag.
                crossReference = new
                {
                    minimumOutcomes = report.CrossReference.MinimumOutcomes,
                    online = report.CrossReference.Online.Select(v => new
                    {
                        verdict = v.Verdict,
                        n = v.Count,
                        meanLaterRating = Round(v.MeanLaterRating, 2),
                        lovedRate = Round(v.LovedRate, 4),
                        dislikedRate = Round(v.DislikedRate, 4),
                        answerable = v.Answerable,
                    }),
                    backtests = report.CrossReference.Backtests.Select(b => new
                    {
                        promptVersion = b.PromptVersion,
                        evaluatedAt = b.EvaluatedAt,
                        judged = b.HoldoutJudged,
                        auc = Round(b.Auc, 3),
                        meanLaterRatingKeep = Round(b.MeanLaterRatingKeep, 2),
                        meanLaterRatingOut = Round(b.MeanLaterRatingOut, 2),
                        keepLovedRate = Round(b.KeepLovedRate, 4),
                        keepDislikedRate = Round(b.KeepDislikedRate, 4),
                        falseRuleOutRate = Round(b.FalseRuleOutRate, 4),
                        falseRuleOutLow = Round(b.FalseRuleOutLow, 4),
                        falseRuleOutHigh = Round(b.FalseRuleOutHigh, 4),
                        trueRuleOutRate = Round(b.TrueRuleOutRate, 4),
                        unsureRate = Round(b.UnsureRate, 4),
                        gating = b.Gating,
                        caveat = b.Caveat,
                        current = b.Current,
                    }),
                },
            });
        });
    }

    private static double? Round(double? value, int digits) =>
        value is null ? null : Math.Round(value.Value, digits, MidpointRounding.AwayFromZero);

    private static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
