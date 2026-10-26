using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>What a cell of the matrix found.</summary>
public enum Verdict
{
    /// <summary>The control's surface showed the violator and the guarded account's did not (or, for a cell that must stay visible, showed it too).</summary>
    Holds,

    /// <summary>The guarded account's surface showed what its exclusion holds out.</summary>
    Leaks,

    /// <summary>A title the exclusion does NOT cover was held out anyway (the stand-in on a medium that has the genre id).</summary>
    OverExcludes,

    /// <summary>The control's surface never showed the violator, so the cell measured nothing. A failure, not a pass.</summary>
    Unexercised,

    /// <summary>By contract the surface may show it (a lookup shows what you have seen; Saved is your own list), so it is reported, not gated.</summary>
    NotGated,

    /// <summary>The pair does not exist for this cell (a not-interested title is never on the Saved shelf).</summary>
    NotApplicable,
}

/// <summary>One cell: an exclusion, a medium, a surface, and whether the exclusion was standing or added late.</summary>
public sealed record MatrixCell(string Exclusion, MediaType Media, string Surface, bool Late)
{
    public string Id => $"{Exclusion}|{Media}|{Surface}|{(Late ? "late" : "standing")}";

    public override string ToString() => Id;
}

/// <summary>
/// The matrix's contract — which surfaces each exclusion binds, and how a verdict is read off the
/// world — in one place, so the expectations are reviewable as a table rather than scattered asserts.
///
/// <para><b>The contract, by surface class</b> (CLAUDE.md's eligibility lock, "a standing exclusion is
/// airtight on every channel", FD-3/FD-5/FD-6 defaults, and WS-4's R1-18 pin rule):</para>
/// <list type="bullet">
/// <item><b>Suggestion surfaces</b> — tonight's picks, feed rows and their pages, genre rails, Similar,
/// the digest, Ask — hold out every rule exclusion, the whole footprint and the For-you bar.</item>
/// <item><b>Lookup surfaces</b> — typeahead and person pages — hold out every rule exclusion (FD-5:
/// "hidden in both legs"; the person page has no decision of its own, and the lock covers every
/// channel), but may show what you have seen: lookup is not suggestion. The bar does not bind a lookup.</item>
/// <item><b>Saved</b> — your own queue: it holds out the rule exclusions (the same lock) and consumed
/// saves (watched or rated since saving), and must SHOW what you saved. The bar does not bind it.</item>
/// <item><b>Watch tonight</b> — the one pinned pick: never a ruled-out or consumed title, and when it is
/// under the For-you bar its reason says so in its first words (R1-18).</item>
/// </list>
/// </summary>
public static class MatrixCells
{
    private static readonly HashSet<string> Suggestion = ["feed-hero", "feed-row", "more", "genre-rail", "similar", "digest", "ask"];
    private static readonly HashSet<string> Lookup = ["typeahead", "person"];

    /// <summary>Every cell the matrix evaluates: (rule kinds + footprint kinds + bar) × media × surfaces × standing/late.</summary>
    public static IEnumerable<MatrixCell> All() =>
        from exclusion in MatrixWorld.RuleKinds.Concat(MatrixWorld.FootprintKinds).Append("bar")
        from media in MatrixWorld.Media
        from surface in MatrixWorld.Surfaces
        from late in new[] { false, true }
        select new MatrixCell(exclusion, media, surface, late);

    /// <summary>Reads one cell's verdict off the world, with the evidence in words.</summary>
    public static (Verdict Verdict, string Evidence) Evaluate(MatrixWorld world, MatrixCell cell)
    {
        var rule = MatrixWorld.RuleKinds.Contains(cell.Exclusion);
        var bar = cell.Exclusion == "bar";

        if (cell.Surface is "saved" or "watch-tonight")
        {
            return Queue(world, cell, rule, bar);
        }

        if (Lookup.Contains(cell.Surface) && !rule)
        {
            return (Verdict.NotGated, "a lookup may show what you have seen, and the bar does not bind a lookup");
        }

        // Every pair that read this surface, in order; the cell is decided by the pairs whose control
        // was shown the violator (exercised), and it leaks if ANY exercised pair's guard was shown it.
        var outcomes = Pairs(cell)
            .Where(p => world.Seen.ContainsKey(p.Control) && world.Seen.ContainsKey(p.Guarded))
            .Select(p => bar ? Bar(world, cell, p.Control, p.Guarded) : Violator(world, cell, p.Control, p.Guarded))
            .ToList();
        var exercised = outcomes.Where(o => o.Item1 != Verdict.Unexercised).ToList();
        if (exercised.Count == 0)
        {
            return (Verdict.Unexercised, outcomes.Count == 0 ? "no pair read this surface" : string.Join("; ", outcomes.Select(o => o.Item2)));
        }

        return exercised.FirstOrDefault(o => o.Item1 is Verdict.Leaks or Verdict.OverExcludes) is { Item2: not null } bad
            ? bad
            : (Verdict.Holds, string.Join("; ", exercised.Select(o => o.Item2)));
    }

    private static (Verdict, string) Violator(MatrixWorld world, MatrixCell cell, string control, string guarded)
    {
        var violator = world.Violators[(cell.Exclusion, cell.Media)];
        var controlSaw = Shown(world, control, cell.Surface, violator.Id, trays: true);
        var guardedSaw = Shown(world, guarded, cell.Surface, violator.Id, trays: true);

        // A film with a horror stand-in keyword is not a horror film (films have the genre id), so it
        // must stay wherever the control shows it.
        if (cell.Exclusion == "genre-standin" && cell.Media == MediaType.Movie)
        {
            return !controlSaw
                ? (Verdict.Unexercised, $"{control} never showed {violator.Name} on {cell.Surface}")
                : guardedSaw
                    ? (Verdict.Holds, $"{violator.Name} stays visible to {guarded}, as it must")
                    : (Verdict.OverExcludes, $"{guarded} lost {violator.Name}, which its rule does not cover");
        }

        if (!controlSaw)
        {
            return (Verdict.Unexercised, $"{control} never showed {violator.Name} on {cell.Surface}");
        }

        return guardedSaw
            ? (Verdict.Leaks, $"{guarded} was shown {violator.Name} on {cell.Surface}")
            : (Verdict.Holds, $"{control} was shown {violator.Name}; {guarded} was not");
    }

    /// <summary>
    /// The (control, guarded) pairs whose surfaces a cell reads: the broad pair, the focus-row pair
    /// (the violator below the decoys, on the rows), and the focus pair (the violator at the top, for
    /// the capped surfaces). Tonight's picks and the digest are read from focus pairs only, because a
    /// broad account's top few are whatever ranks highest, not the violator under test.
    /// </summary>
    private static IEnumerable<(string Control, string Guarded)> Pairs(MatrixCell cell)
    {
        var family = MatrixWorld.RuleKinds.Contains(cell.Exclusion) ? MatrixWorld.Family.Rules
            : cell.Exclusion == "bar" ? MatrixWorld.Family.Bar : MatrixWorld.Family.Footprint;
        if (!MatrixWorld.CappedSurfaces.Contains(cell.Surface))
        {
            yield return (MatrixWorld.Broad(family, guarded: false, late: false), MatrixWorld.Broad(family, guarded: true, cell.Late));
        }

        foreach (var rows in new[] { true, false })
        {
            yield return (MatrixWorld.Focus(cell.Exclusion, guarded: false, late: false, rows), MatrixWorld.Focus(cell.Exclusion, guarded: true, cell.Late, rows));
        }
    }

    private static bool Shown(MatrixWorld world, string label, string surface, Guid titleId, bool trays) =>
        world.Seen.TryGetValue(label, out var seen)
        && (seen[surface].Contains(titleId)
            || (trays && surface == "ask" && world.AskTrays.TryGetValue(label, out var tray) && tray.Contains(titleId)));

    /// <summary>
    /// The For-you bar on a suggestion surface: no card of the cell's medium the guarded account is
    /// predicted under the bar. Ask's labelled below-floor tray is the bar's own disclosure and is not
    /// counted. Exercised when the control was shown such a card.
    /// </summary>
    private static (Verdict, string) Bar(MatrixWorld world, MatrixCell cell, string control, string guarded)
    {
        var under = (string label) => world.Seen[label][cell.Surface].Distinct()
            .Where(id => world.MediaOf(id) == cell.Media
                && world.Predictions[label].TryGetValue(id, out var p) && Badge(p) < world.Bar)
            .ToList();
        var controlUnder = under(control);
        if (controlUnder.Count == 0)
        {
            return (Verdict.Unexercised, $"{control} was shown no {cell.Media} under {world.Bar} on {cell.Surface}");
        }

        var guardedUnder = under(guarded);
        var disclosed = cell.Surface is "feed-hero" or "feed-row" && world.FeedDegraded.GetValueOrDefault(guarded) is { } reason
            ? $" (the feed says: \"{reason}\")"
            : string.Empty;
        return guardedUnder.Count > 0
            ? (Verdict.Leaks, $"{guarded} was shown {guardedUnder.Count} {cell.Media} card(s) predicted under its bar {world.Bar} on {cell.Surface}, lowest {guardedUnder.Min(id => Badge(world.Predictions[guarded][id])):0.0}{disclosed}")
            : (Verdict.Holds, $"{control} was shown {controlUnder.Count} under {world.Bar}; {guarded} none");
    }

    /// <summary>
    /// A prediction as the card's badge shows it (one decimal), which is what a bar is held to: a card
    /// that reads "8.7" under an 8.7 bar shows nothing the user ruled out (the supply truth's rule too).
    /// </summary>
    private static decimal Badge(decimal predicted) => Math.Round(predicted, 1, MidpointRounding.AwayFromZero);

    /// <summary>The Saved shelf and the Watch-tonight pin, read from pin pairs (a queue of one title).</summary>
    private static (Verdict, string) Queue(MatrixWorld world, MatrixCell cell, bool rule, bool bar)
    {
        if (cell.Late)
        {
            return (Verdict.NotApplicable, "the queue and the pin are computed on every read; a late exclusion is a standing one");
        }

        if (bar)
        {
            if (cell.Surface == "saved")
            {
                return (Verdict.NotGated, "Saved is your own list; the bar does not bind it");
            }

            var control = world.Pins[MatrixWorld.Pin("bar", cell.Media, guarded: false)];
            var pin = world.Pins[MatrixWorld.Pin("bar", cell.Media, guarded: true)];
            var label = MatrixWorld.Pin("bar", cell.Media, guarded: true);
            if (control is null)
            {
                return (Verdict.Unexercised, "the control pinned nothing");
            }

            if (pin is not { } pinned || !world.Predictions[label].TryGetValue(pinned.TitleId, out var predicted) || Badge(predicted) >= world.Bar)
            {
                return (Verdict.Unexercised, $"{label} pinned nothing under the bar {world.Bar}");
            }

            var says = pinned.Reason is { } reason
                && (reason.Contains("is under", StringComparison.Ordinal) || reason.StartsWith("Nothing in your queue", StringComparison.Ordinal));
            return says
                ? (Verdict.Holds, $"pinned at {predicted:0.0} under {world.Bar}, and the reason says so: \"{pin.Value.Reason}\"")
                : (Verdict.Leaks, $"pinned at {predicted:0.0} under {world.Bar} with a reason that reads as a recommendation: \"{pin.Value.Reason}\"");
        }

        string kind;
        if (rule)
        {
            kind = cell.Exclusion;
        }
        else if (cell.Exclusion is "watched" or "rated")
        {
            kind = cell.Exclusion;
        }
        else if (cell.Exclusion == "saved")
        {
            if (cell.Surface == "watch-tonight")
            {
                return (Verdict.NotGated, "a saved title may be the pin");
            }

            // Saved must SHOW what you saved: the broad footprint account saved its violator.
            var savedId = world.Violators[("saved", cell.Media)].Id;
            var guardedLabel = MatrixWorld.Broad(MatrixWorld.Family.Footprint, guarded: true, late: false);
            return world.Seen[guardedLabel]["saved"].Contains(savedId)
                ? (Verdict.Holds, "the saved title is on the shelf")
                : (Verdict.Leaks, "the saved title is MISSING from the Saved shelf");
        }
        else
        {
            return (Verdict.NotApplicable, $"a {cell.Exclusion} title is not on the queue");
        }

        var controlLabel = MatrixWorld.Pin(kind, cell.Media, guarded: false);
        var guardedPin = MatrixWorld.Pin(kind, cell.Media, guarded: true);
        var violator = rule ? world.Violators[($"saved-{kind}", cell.Media)] : world.Violators[(kind, cell.Media)];
        var controlSaw = world.Seen[controlLabel][cell.Surface].Contains(violator.Id);
        var guardedSaw = world.Seen[guardedPin][cell.Surface].Contains(violator.Id);
        if (cell.Exclusion == "genre-standin" && cell.Media == MediaType.Movie)
        {
            return !controlSaw
                ? (Verdict.Unexercised, $"{controlLabel} never showed {violator.Name}")
                : guardedSaw ? (Verdict.Holds, $"{violator.Name} stays, as it must") : (Verdict.OverExcludes, $"{guardedPin} lost {violator.Name}");
        }

        if (!controlSaw)
        {
            return (Verdict.Unexercised, $"{controlLabel} never showed {violator.Name} on {cell.Surface}");
        }

        return guardedSaw
            ? (Verdict.Leaks, $"{guardedPin} was shown {violator.Name} on {cell.Surface}")
            : (Verdict.Holds, $"{controlLabel} was shown {violator.Name}; {guardedPin} was not");
    }
}
