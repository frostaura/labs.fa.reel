using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Domain.Providers;

namespace FrostAura.Reel.Tests.Providers;

/// <summary>
/// IR2S-02: an offer is new only when the provider set changed to include it. A refetch that finds
/// the same offer must carry its FirstSeenAt — null included — and a first look must stamp nothing,
/// because Reel cannot know when an offer it has always seen began.
/// </summary>
public class AvailabilitySnapshotTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Netflix = Guid.Parse("00000000-0000-0000-0000-000000000008");
    private static readonly Guid Hulu = Guid.Parse("00000000-0000-0000-0000-000000000015");

    private static TitleAvailability Row(Guid provider, AvailabilityKind kind, DateTime? firstSeenAt) => new()
    {
        Id = Guid.NewGuid(),
        TitleId = Guid.NewGuid(),
        Region = "US",
        ProviderId = provider,
        Kind = kind,
        FetchedAt = Now.AddDays(-2),
        FirstSeenAt = firstSeenAt,
    };

    [Fact]
    public void A_first_look_calls_nothing_new()
    {
        // The NCIS case: no previous snapshot, a Flatrate offer on Netflix. Long-running, so "new"
        // would be a lie — and Reel has no way to tell it from a genuine arrival on a first look.
        Assert.Null(AvailabilitySnapshot.FirstSeenAt([], Netflix, AvailabilityKind.Flatrate, Now));
    }

    [Fact]
    public void A_refetch_of_a_baseline_offer_stays_baseline()
    {
        var previous = new[] { Row(Netflix, AvailabilityKind.Flatrate, firstSeenAt: null) };
        Assert.Null(AvailabilitySnapshot.FirstSeenAt(previous, Netflix, AvailabilityKind.Flatrate, Now));
    }

    [Fact]
    public void A_refetch_carries_an_observed_arrival_unchanged()
    {
        var arrived = Now.AddDays(-12);
        var previous = new[] { Row(Netflix, AvailabilityKind.Flatrate, arrived) };

        // Not restamped to now: the arrival was twelve days ago, and stays twelve days ago however
        // often the cache is refreshed.
        Assert.Equal(arrived, AvailabilitySnapshot.FirstSeenAt(previous, Netflix, AvailabilityKind.Flatrate, Now));
    }

    [Fact]
    public void A_provider_the_previous_snapshot_lacked_is_stamped_now()
    {
        var previous = new[] { Row(Netflix, AvailabilityKind.Flatrate, firstSeenAt: null) };
        Assert.Equal(Now, AvailabilitySnapshot.FirstSeenAt(previous, Hulu, AvailabilityKind.Flatrate, Now));
    }

    [Fact]
    public void Moving_from_rent_to_subscription_on_the_same_provider_is_an_arrival()
    {
        // The common real arrival: a title that was rent/buy only becomes part of a subscription.
        var previous = new[]
        {
            Row(Netflix, AvailabilityKind.Rent, firstSeenAt: null),
            Row(Hulu, AvailabilityKind.Buy, firstSeenAt: null),
        };
        Assert.Equal(Now, AvailabilitySnapshot.FirstSeenAt(previous, Netflix, AvailabilityKind.Flatrate, Now));
        Assert.Null(AvailabilitySnapshot.FirstSeenAt(previous, Netflix, AvailabilityKind.Rent, Now));
    }
}
