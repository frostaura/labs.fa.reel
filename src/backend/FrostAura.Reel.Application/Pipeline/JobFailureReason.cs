using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Pipeline;

/// <summary>
/// Maps a job's persisted error text to a stable reason code the frontend renders as fixed copy.
/// The error column is free text from whichever exception ended the attempt, so it never reaches
/// the screen; the code is the contract. Ordered most specific first, and anything unrecognised is
/// <see cref="Error"/> — a defined fallback, never a guess.
/// </summary>
public static partial class JobFailureReason
{
    public const string Stalled = "stalled";
    public const string PaymentRequired = "payment_required";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string NoAnswer = "no_answer";
    public const string BudgetSpent = "budget_spent";
    public const string TimedOut = "timed_out";
    public const string Error = "error";

    /// <summary>Every code <see cref="Classify"/> can return.</summary>
    public static readonly IReadOnlyList<string> All =
        [Stalled, PaymentRequired, Unauthorized, Forbidden, RateLimited, NoAnswer, BudgetSpent, TimedOut, Error];

    public static string Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return Error;
        }

        if (error.Contains("Stalled with no heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            return Stalled;
        }

        if (Status402().IsMatch(error) || error.Contains("Payment Required", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentRequired;
        }

        if (Status401().IsMatch(error) || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized;
        }

        if (Status403().IsMatch(error) || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return Forbidden;
        }

        if (Status429().IsMatch(error) || error.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimited;
        }

        if (error.Contains("answered none", StringComparison.OrdinalIgnoreCase))
        {
            return NoAnswer;
        }

        if (error.Contains("ceiling", StringComparison.OrdinalIgnoreCase)
            || error.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            return BudgetSpent;
        }

        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("was cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return TimedOut;
        }

        return Error;
    }

    [GeneratedRegex(@"(?<![\w.-])402(?![\w-]|\.\d)")]
    private static partial Regex Status402();

    [GeneratedRegex(@"(?<![\w.-])401(?![\w-]|\.\d)")]
    private static partial Regex Status401();

    [GeneratedRegex(@"(?<![\w.-])403(?![\w-]|\.\d)")]
    private static partial Regex Status403();

    [GeneratedRegex(@"(?<![\w.-])429(?![\w-]|\.\d)")]
    private static partial Regex Status429();
}
