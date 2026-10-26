namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>
/// The exclusion matrix's open leaks, each with the defect it is and the workstream that owns the fix.
/// A pattern is <c>exclusion|medium|surface|timing</c>, where any segment may be <c>*</c>. Entries come
/// OUT as the owners land fixes: <see cref="ExclusionMatrixTests"/> fails on a ledgered cell that has
/// stopped leaking, so this list can only shrink honestly.
/// </summary>
public static class MatrixLedger
{
    private const string PersonPage =
        "XM-01 (WS-5, PersonEndpoints): the person page lists the whole filmography with no content rule applied, so an " +
        "excluded genre, keyword, language or crowd-floor title is shown. A lookup may show what you have seen, never what " +
        "you ruled out (the airtight lock; FD-5's default for the other lookup, typeahead).";

    private const string Queue =
        "XM-02 (WS-4, SavedEndpoints / SavedQueue): the Saved shelf and the Watch-tonight pin apply no content rule, so a " +
        "saved title in an excluded genre, keyword or language, or under the crowd floor, stays on the shelf and can be " +
        "pinned as tonight's pick.";

    private const string LateFeed =
        "XM-03 (WS-5, FeedEndpoints GET /api/feed): the materialised snapshot is pruned of new footprint on read, but not of " +
        "a content rule saved after the build, so tonight's picks and the rows keep serving (and logging exposures for) a " +
        "title the user has just ruled out until the next build.";

    private const string LateDigest =
        "XM-04 (WS-4, DigestEndpoints GET /api/digest/current): the stored digest is served as built, with no read-time " +
        "eligibility, so a title watched, rated, saved, dismissed or dropped, or ruled out by a content rule, after the " +
        "weekly build stays in the digest all week.";

    private const string DigestBar =
        "XM-05 (WS-4, BuildDigestJobHandler): the digest ranks against its own 7.0 floor, relaxed to 6.0, and never reads " +
        "the account's For-you bar, so it carries cards under the bar the user set (and keeps them after the bar is raised).";

    private const string FeedBar =
        "XM-07 (WS-5, BuildFeedJobHandler / FeedEndpoints): when fewer than 20 titles clear the For-you bar the build relaxes " +
        "it to the shortlist's 80th percentile and says so in degradedReason, while FD-4's default is that cards below the " +
        "bar never reach a main grid; and a bar saved after the build is never applied to the materialised picks and rows. " +
        "The owner reconciles the soft floor with the FD-4 default.";

    /// <summary>The rule kinds that leak wherever a rule is ignored (the stand-in only on TV; on film it must stay visible).</summary>
    private static readonly string[] Rules = ["genre|*", "genre-standin|Show", "keyword|*", "language|*", "crowd|*"];

    private static readonly string[] Footprint = ["watched", "rated", "not-interested", "dropped", "saved"];

    private static readonly (string Pattern, string Defect)[] Known =
    [
        .. Rules.Select(r => ($"{r}|person|*", PersonPage)),
        .. Rules.Select(r => ($"{r}|saved|standing", Queue)),
        .. Rules.Select(r => ($"{r}|watch-tonight|standing", Queue)),
        .. Rules.Select(r => ($"{r}|feed-hero|late", LateFeed)),
        .. Rules.Select(r => ($"{r}|feed-row|late", LateFeed)),
        .. Rules.Select(r => ($"{r}|digest|late", LateDigest)),
        .. Footprint.Select(f => ($"{f}|*|digest|late", LateDigest)),
        ("bar|*|digest|*", DigestBar),
        ("bar|Show|feed-hero|standing", FeedBar),
        ("bar|*|feed-hero|late", FeedBar),
        ("bar|*|feed-row|late", FeedBar),
    ];

    /// <summary>The defect a cell is ledgered under, or null.</summary>
    public static string? Find(MatrixCell cell) =>
        Known.FirstOrDefault(k => Matches(k.Pattern, cell.Id)).Defect;

    private static bool Matches(string pattern, string id)
    {
        var p = pattern.Split('|');
        var c = id.Split('|');
        return p.Length == c.Length && p.Zip(c).All(x => x.First == "*" || x.First == x.Second);
    }
}
