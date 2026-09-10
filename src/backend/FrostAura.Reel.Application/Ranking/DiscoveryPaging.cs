namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// How deep into TMDB a discovery call reads — the one place the "consumption buys depth"
/// arithmetic lives.
///
/// <para>
/// The rule: a user who has reacted to and rated a hundred things since the model was trained has
/// already been shown the head of every popularity-ordered list the build fetches, so fetching page
/// 1 again returns titles that are, for that user, already spent. Each ~20 items consumed advances
/// the READ HEAD by a page; each ~60 buys an extra page of BREADTH per arm. Depth grows faster than
/// breadth on purpose — walking further into a list the user is demonstrably working through is
/// cheaper and better-targeted than fetching more of the same shallow head.
/// </para>
///
/// <para>
/// <see cref="FeedPaginator"/>'s scroll-deepening already used this idea and owned its own copy of
/// the arithmetic; the build path had no copy at all and read page 1 for ever. Both now read from
/// here, so the two cannot drift and the constants can be argued about in one place.
/// </para>
/// </summary>
public static class DiscoveryPaging
{
    /// <summary>Items consumed per page the read head advances.</summary>
    private const int ItemsPerPage = 20;

    /// <summary>Items consumed per EXTRA page of breadth an arm fetches in one build.</summary>
    private const int ItemsPerExtraPage = 60;

    /// <summary>
    /// Pages one build will ever pull from a single arm. Five is a call-budget decision, not a TMDB
    /// one: at four genre arms that is already 20 of a ~40-call build, and the arms below it (seeds,
    /// person, keyword) are the ones that make the pool personal rather than merely bigger.
    /// </summary>
    public const int MaxBuildPages = 5;

    /// <summary>The first page an arm fetches. Page 1 while nothing has been consumed.</summary>
    public static int StartPage(int consumed) => 1 + (Math.Max(0, consumed) / ItemsPerPage);

    /// <summary>How many consecutive pages one arm fetches this build, 1..<see cref="MaxBuildPages"/>.</summary>
    public static int PageCount(int consumed) =>
        Math.Clamp(1 + (Math.Max(0, consumed) / ItemsPerExtraPage), 1, MaxBuildPages);

    /// <summary>
    /// The page a paginated ROW deepens to as the user scrolls. Never below 2 — page 1 is what the
    /// row is already showing — and never past the caller's own ceiling.
    /// </summary>
    public static int DeepenPage(int seen, int maxPage) => Math.Clamp(StartPage(seen) + 1, 2, maxPage);

    /// <summary>
    /// The page a sweep with NO per-user consumption signal reads this day. The global catalogue
    /// sweep is tenant-shared, so there is no "consumed" to scale by; rotating deterministically by
    /// day is what stops a weekly pass re-fetching the identical page-1 titles it already holds,
    /// while staying replayable from the timestamp alone.
    /// </summary>
    /// <param name="rotationOffset">A day-granular offset — <see cref="AnchorRotation.OffsetFor"/>.</param>
    /// <param name="span">How many pages the rotation walks before wrapping.</param>
    public static int RotatedPage(int rotationOffset, int span) =>
        1 + (Math.Abs(rotationOffset) % Math.Max(1, span));
}
