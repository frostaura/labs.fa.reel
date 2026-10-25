using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FrostAura.Reel.Application.Configuration;

/// <summary>
/// The house pattern for a typed options class (MOD-2): one class per config concern, read from the
/// flat <c>UPPER_SNAKE</c> keys compose actually passes, and validated when the host STARTS rather
/// than on the first request that happens to need it.
///
/// <para><b>Why it exists.</b> Forty-odd files read <see cref="IConfiguration"/> by string and fall
/// back to a default on anything unparseable. That makes a typo in a compose file — or a key that
/// never reached the container at all (K-5) — indistinguishable from the documented default: the
/// process runs, quietly, on numbers nobody chose. A class derived from this one turns the same
/// typo into a startup failure that names the key.</para>
///
/// <para><b>How to add one.</b> Derive, read every key in <see cref="Read"/> through the
/// <see cref="SettingReader"/> (which records an unparseable or out-of-range value as a problem
/// instead of silently defaulting it), and register it in your own
/// <c>DependencyInjection.&lt;Area&gt;.cs</c> with
/// <see cref="ValidatedOptionsRegistration.AddValidatedOptions{TOptions}"/>. Consume it as
/// <c>IOptions&lt;T&gt;</c>. Every key read here must also appear in all four compose descriptors —
/// <c>ComposeEnvParityTests</c> fails the build otherwise.</para>
///
/// <para>A key that is ABSENT still takes its default: absence is a choice compose makes visibly
/// (the descriptors spell every default out). Only a value that is present and wrong fails.</para>
/// </summary>
public abstract class ValidatedOptions
{
    private readonly List<string> problems = [];

    /// <summary>Every problem found while reading, one sentence each, naming the key. Empty when valid.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Reads every setting this class owns. Called once per bind.</summary>
    protected abstract void Read(SettingReader reader);

    /// <summary>Cross-field checks that run after <see cref="Read"/>. Report through <paramref name="problem"/>.</summary>
    protected virtual void Check(Action<string> problem)
    {
    }

    /// <summary>Binds <paramref name="options"/> from <paramref name="configuration"/>, replacing any earlier result.</summary>
    internal static void Bind(ValidatedOptions options, IConfiguration configuration)
    {
        options.problems.Clear();
        options.Read(new SettingReader(configuration, options.problems));
        options.Check(options.problems.Add);
    }

    /// <summary>
    /// Builds and binds an instance outside DI — for tests and for the few call sites that construct
    /// a service by hand. The result may carry <see cref="Problems"/>; nothing throws here.
    /// </summary>
    public static TOptions From<TOptions>(IConfiguration configuration)
        where TOptions : ValidatedOptions, new()
    {
        var options = new TOptions();
        Bind(options, configuration);
        return options;
    }
}

/// <summary>
/// Reads flat configuration keys with invariant parsing, recording every present-but-invalid value
/// as a problem rather than falling back.
/// </summary>
public sealed class SettingReader(IConfiguration configuration, List<string> problems)
{
    /// <summary>
    /// The value exactly as configured, or null when the key is absent or blank. Never trimmed: a
    /// secret with surrounding whitespace is a different secret, and quietly signing with a trimmed
    /// copy would invalidate every session it had issued. The number and boolean readers tolerate
    /// surrounding whitespace themselves.
    /// </summary>
    public string? Raw(string key) =>
        configuration[key] is { } raw && !string.IsNullOrWhiteSpace(raw) ? raw : null;

    /// <summary>An optional string; absent or blank gives <paramref name="fallback"/>.</summary>
    public string String(string key, string fallback) => Raw(key) ?? fallback;

    /// <summary>A string that must be present.</summary>
    public string RequiredString(string key, int minLength = 1)
    {
        var value = Raw(key);
        if (value is null)
        {
            problems.Add($"{key} is required.");
            return string.Empty;
        }

        if (value.Length < minLength)
        {
            problems.Add($"{key} must be at least {minLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        return value;
    }

    /// <summary>A whole number within [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public int Int(string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            problems.Add($"{key} must be a whole number; got a value that does not parse.");
            return fallback;
        }

        if (parsed < min || parsed > max)
        {
            // The value is not echoed: problems are logged, and a mis-pasted secret can parse as a number.
            problems.Add($"{key} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
            return fallback;
        }

        return parsed;
    }

    /// <summary>A boolean (<c>true</c>/<c>false</c>, case-insensitive).</summary>
    public bool Bool(string key, bool fallback)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return fallback;
        }

        if (!bool.TryParse(raw.Trim(), out var parsed))
        {
            problems.Add($"{key} must be true or false.");
            return fallback;
        }

        return parsed;
    }
}

/// <summary>DI registration for <see cref="ValidatedOptions"/> classes.</summary>
public static class ValidatedOptionsRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TOptions"/> bound from the root configuration and validated on
    /// host start: a present-but-invalid value stops the process before it listens, with every
    /// problem named in the exception.
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(this IServiceCollection services)
        where TOptions : ValidatedOptions, new()
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, ProblemsValidator<TOptions>>());
        return services.AddOptions<TOptions>()
            .Configure<IConfiguration>((options, configuration) => ValidatedOptions.Bind(options, configuration))
            .ValidateOnStart();
    }

    private sealed class ProblemsValidator<TOptions> : IValidateOptions<TOptions>
        where TOptions : ValidatedOptions
    {
        public ValidateOptionsResult Validate(string? name, TOptions options) =>
            options.Problems.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(options.Problems.Select(p => $"{typeof(TOptions).Name}: {p}"));
    }
}
