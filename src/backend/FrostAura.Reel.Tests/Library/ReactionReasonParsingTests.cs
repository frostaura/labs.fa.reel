using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Tests.Library;

/// <summary>
/// The wire → enum mapping for not-interested reasons. "Just not for me" used to send no reason at
/// all (and the parser had no member for it), so the catch-all choice was indistinguishable from a
/// reasonless dismissal in the data.
/// </summary>
public class ReactionReasonParsingTests
{
    [Theory]
    [InlineData("genre", ReactionReason.Genre)]
    [InlineData("seen-enough", ReactionReason.SeenEnough)]
    [InlineData("seen_enough", ReactionReason.SeenEnough)]
    [InlineData("tone", ReactionReason.Tone)]
    [InlineData("not-for-me", ReactionReason.NotForMe)]
    [InlineData("not_for_me", ReactionReason.NotForMe)]
    [InlineData("NOT-FOR-ME", ReactionReason.NotForMe)]
    public void Known_wire_values_map(string wire, ReactionReason expected) =>
        Assert.Equal(expected, ReactionEndpoints.ParseReason(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void Unknown_values_yield_no_reason(string? wire) =>
        Assert.Null(ReactionEndpoints.ParseReason(wire));
}
