using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>WS-5 task 12 (N-8): a show's season and episode ratings roll up into one show-level signal.</summary>
public class ShowRatingRollupTests
{
    private static readonly Guid Show = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ShowRatingRollup.Part P(RatingSubjectType subject, short rating, int day = 0) =>
        new(Show, subject, rating, At.AddDays(day));

    [Fact]
    public void Season_ratings_are_the_summary_and_win_over_episodes()
    {
        var rolled = ShowRatingRollup.Roll(
            [P(RatingSubjectType.Season, 9), P(RatingSubjectType.Season, 8, 3), P(RatingSubjectType.Episode, 2, 5)],
            new HashSet<Guid>(), userMean: 7m);

        var only = Assert.Single(rolled);
        Assert.Equal((short)9, only.Rating); // 8.5 rounds away from zero
        Assert.Equal(At.AddDays(5), only.RatedAt); // dated at the newest constituent
    }

    [Fact]
    public void Episode_only_ratings_are_shrunk_toward_the_account_mean()
    {
        // Three 10s against a 6.0 reader: (30 + 3 × 6) / 6 = 8.
        var rolled = ShowRatingRollup.Roll(
            [P(RatingSubjectType.Episode, 10), P(RatingSubjectType.Episode, 10, 1), P(RatingSubjectType.Episode, 10, 2)],
            new HashSet<Guid>(), userMean: 6m);

        Assert.Equal((short)8, Assert.Single(rolled).Rating);
    }

    [Fact]
    public void A_show_rated_as_a_whole_is_never_rolled_up()
    {
        Assert.Empty(ShowRatingRollup.Roll([P(RatingSubjectType.Season, 3)], new HashSet<Guid> { Show }, userMean: 7m));
    }
}
