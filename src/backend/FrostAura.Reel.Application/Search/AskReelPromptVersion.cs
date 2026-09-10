namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The manual bump lever for everything Ask Reel caches on behalf of a prompt.
///
/// <para><b>Bump <see cref="Current"/> with ANY edit to the interpret or re-rank prompt</b> — the
/// system message, the user message, the schema, the temperature, the token ceiling. Ask Reel's
/// caches are keyed on the request, not on the thing that answered it, so without a version in the
/// key a prompt improvement is invisible for the cache's whole TTL: the old answer keeps being
/// served to the exact query the change was made for, which is the one the author will test with.
/// The same reasoning, and the same lever, as <c>TitleLensCopy.PromptVersion</c>; the difference is
/// only that these caches are in-memory and unversioned rows cannot be inspected after the fact.</para>
///
/// <para>The model id rides beside it at every key site rather than inside this constant, because it
/// changes for reasons that have nothing to do with the prompt (a provider swap, a founder-locked
/// model bump) and the two must be able to move independently.</para>
///
/// <para>A date is used rather than an integer so a stale cached answer can be dated at a glance,
/// and so two branches editing the prompt in the same week collide in review instead of silently
/// agreeing on "3".</para>
/// </summary>
public static class AskReelPromptVersion
{
    /// <summary>The current Ask Reel prompt generation. Bump on any prompt edit — see the type doc.</summary>
    public const string Current = "askreel-2026-09-11";
}
