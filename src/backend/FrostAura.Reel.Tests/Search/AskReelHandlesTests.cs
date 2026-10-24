using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The handles "the second one" resolves against. A-4: "is the first one violent?" was answered
/// about Replicas, the fifth card on screen, because references resolved against the no-repeat set
/// ordered by TMDB id.
/// </summary>
public class AskReelHandlesTests
{
    private static readonly PresentedCard[] Answer2 =
    [
        new(2, 2, Guid.Parse("00000000-0000-0000-0000-000000000002"), "John Wick: Chapter 4", 2023, MediaType.Movie),
        new(2, 1, Guid.Parse("00000000-0000-0000-0000-000000000001"), "Ballerina", 2025, MediaType.Movie),
    ];

    private static readonly PresentedCard[] Answer1 =
    [
        new(1, 1, Guid.Parse("00000000-0000-0000-0000-000000000003"), "Replicas", null, MediaType.Movie),
    ];

    [Fact]
    public void A_line_names_the_answer_the_position_and_the_title()
    {
        Assert.Equal("T2.1 Ballerina (2025)", Answer2[1].Line);
        Assert.Equal("T1.1 Replicas", Answer1[0].Line);
    }

    [Fact]
    public void The_prompt_reads_the_newest_answer_first_in_screen_order() =>
        Assert.Equal(
            ["T2.1 Ballerina (2025)", "T2.2 John Wick: Chapter 4 (2023)", "T1.1 Replicas"],
            AskReelHandles.PromptLines([.. Answer1, .. Answer2]));

    [Fact]
    public void A_history_line_carries_what_the_answer_showed()
    {
        Assert.Equal(
            " [Shown: T2.1 Ballerina (2025) · T2.2 John Wick: Chapter 4 (2023)]",
            AskReelHandles.HistorySuffix(Answer2));
        Assert.Equal(string.Empty, AskReelHandles.HistorySuffix([]));
    }

    [Fact]
    public void The_digest_is_a_function_of_the_handles_not_of_row_order()
    {
        Assert.Equal("none", AskReelHandles.Digest([]));
        Assert.Equal(AskReelHandles.Digest([.. Answer2, .. Answer1]), AskReelHandles.Digest([.. Answer1, .. Answer2]));

        // The same titles at swapped positions are a different list to resolve "the second one" in.
        PresentedCard[] swapped =
        [
            Answer2[0] with { Rank = 1 },
            Answer2[1] with { Rank = 2 },
        ];
        Assert.NotEqual(AskReelHandles.Digest(Answer2), AskReelHandles.Digest(swapped));
    }

    [Fact]
    public void The_prompt_is_capped()
    {
        var many = Enumerable.Range(1, 40)
            .Select(i => new PresentedCard(1, i, Guid.NewGuid(), $"Title {i}", 2000, MediaType.Movie))
            .ToList();
        Assert.Equal(AskReelHandles.MaxPromptLines, AskReelHandles.PromptLines(many).Count);
    }
}
