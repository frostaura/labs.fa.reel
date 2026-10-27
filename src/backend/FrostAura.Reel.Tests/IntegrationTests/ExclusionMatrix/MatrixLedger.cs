namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>
/// The exclusion matrix's open leaks, each with the defect it is and the workstream that owns the fix.
/// A pattern is <c>exclusion|medium|surface|timing</c>, where any segment may be <c>*</c>. Entries come
/// OUT as the owners land fixes: <see cref="ExclusionMatrixTests"/> fails on a ledgered cell that has
/// stopped leaking, so this list can only shrink honestly.
/// </summary>
/// <remarks>
/// Empty since trust-v1 wave B: XM-01, XM-03 and XM-07 were closed by WS-5, XM-02, XM-04 and XM-05 by
/// WS-4, and XM-06 by WS-2. A new leak is added here as <c>(pattern, defect)</c>, naming the owner.
/// </remarks>
public static class MatrixLedger
{
    private static readonly (string Pattern, string Defect)[] Known = [];

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
