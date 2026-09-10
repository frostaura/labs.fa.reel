using System.Globalization;
using System.Runtime.CompilerServices;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Pins the test assembly to the invariant culture before the first test runs, mirroring what
/// <c>Program.cs</c> does at host start.
///
/// <para>
/// This is not cosmetic. The dev Mac's locale uses a comma decimal separator, and until
/// 2026-09-10 that leaked into assertion output — a quarantined failure read
/// <c>MatchScore = 4,196591281636216</c>, which cost a reader real time deciding whether the
/// separator WAS the bug. It was not, but nothing about the message said so. Pinning here makes
/// a red test say the same thing on every machine.
/// </para>
///
/// <para>
/// It deliberately does NOT make the suite blind to culture regressions:
/// <see cref="InvariantFormattingTests"/> sets a comma-decimal culture explicitly, per test, and
/// asserts the formatting helpers ignore it. An implicit ambient culture proves nothing either
/// way; an explicit hostile one proves the contract.
/// </para>
/// </summary>
internal static class InvariantCultureModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
