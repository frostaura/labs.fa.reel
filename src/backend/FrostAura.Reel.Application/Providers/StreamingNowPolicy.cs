using FrostAura.Reel.Domain.Providers;

namespace FrostAura.Reel.Application.Providers;

/// <summary>
/// The single definition of "streaming now": what a title must have in an account's region before
/// any surface may say it can be put on tonight without paying for it again.
///
/// <para><b>Which offers count.</b> A subscription (<see cref="AvailabilityKind.Flatrate"/>) and a
/// free offer, with or without ads (<see cref="AvailabilityKind.Free"/>, <see cref="AvailabilityKind.Ads"/>).
/// Rent and Buy never do: "streaming now" is a promise that pressing play costs nothing more, and a
/// rental is a purchase. Free and ad-supported offers used to be left out, so a title on Tubi and Roku
/// alone read as "not streaming" while being the easiest thing in the queue to watch (G-2).</para>
///
/// <para><b>How fresh.</b> An offer is believed for <see cref="FreshFor"/> after it was fetched. Past
/// that the row says what was true a week ago, which is not "now".</para>
///
/// <para>Every surface that says "streaming now" reads it from here — Saved and Watch tonight today;
/// the feed's provider read and the digest are change requests to their owners — so the shelf, the
/// card and the reason line can never disagree about the same title.</para>
/// </summary>
public static class StreamingNowPolicy
{
    /// <summary>How long a fetched offer is believed.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    /// <summary>The offer kinds that count, in the order a surface should prefer to name them.</summary>
    public static IReadOnlyList<AvailabilityKind> Kinds { get; } =
        [AvailabilityKind.Flatrate, AvailabilityKind.Free, AvailabilityKind.Ads];

    /// <summary>Whether an offer of <paramref name="kind"/> counts as streaming now.</summary>
    public static bool Counts(AvailabilityKind kind) =>
        kind is AvailabilityKind.Flatrate or AvailabilityKind.Free or AvailabilityKind.Ads;

    /// <summary>Whether an offer fetched at <paramref name="fetchedAt"/> is still believed at <paramref name="nowUtc"/>.</summary>
    public static bool IsFresh(DateTime fetchedAt, DateTime nowUtc) => fetchedAt > nowUtc - FreshFor;

    /// <summary>
    /// The streaming-now offers among <paramref name="rows"/> for <paramref name="titleIds"/> in
    /// <paramref name="region"/>. Spelled as an OR of equalities rather than a list
    /// <c>Contains</c>, so it translates the same way whatever converter the enum column carries.
    /// </summary>
    public static IQueryable<TitleAvailability> Offers(
        IQueryable<TitleAvailability> rows, IReadOnlyCollection<Guid> titleIds, string region, DateTime nowUtc)
    {
        var cutoff = nowUtc - FreshFor;
        return rows.Where(a => titleIds.Contains(a.TitleId)
            && a.Region == region
            && a.FetchedAt > cutoff
            && (a.Kind == AvailabilityKind.Flatrate || a.Kind == AvailabilityKind.Free || a.Kind == AvailabilityKind.Ads));
    }
}
