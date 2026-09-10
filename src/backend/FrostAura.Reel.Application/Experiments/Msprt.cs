namespace FrostAura.Reel.Application.Experiments;

/// <summary>
/// The mixture sequential probability ratio test over a stream of binary preferences — the piece
/// that lets the founder look at the interleaving number whenever he likes without inflating the
/// error rate.
///
/// <para><b>What it tests.</b> Each DECIDED slate contributes one indicator: 1 when team A earned
/// more outcome credit on that slate, 0 when team B did. Ties are not observations and never enter
/// the stream. The null is that the two rankers are indistinguishable, p = ½.</para>
///
/// <para><b>Why it is exactly valid and not approximately valid.</b> Write Z_n = Σ(x_i − ½). For a
/// bounded increment of range 1, Hoeffding's lemma gives E[e^{λ(x−½)}] ≤ e^{λ²/8} exactly — and
/// λ²/8 = λ²σ²/2 at σ² = ¼, the Bernoulli(½) variance. So exp(λZ_n − λ²nσ²/2) is a nonnegative
/// supermartingale for every λ, and so is any mixture of them. Mixing λ ~ N(0, s²) closes in
/// <see cref="LogLambda"/>, and Ville's inequality then bounds P(∃n : Λ_n ≥ 1/α) ≤ α. The
/// consequence is the one that matters: <b>peeking at every single slate costs nothing.</b> This is
/// not a normal approximation whose type-I rate has to be hoped for — the simulation in the test
/// suite measures it anyway, because a stated proof and a measured rate are different evidence.</para>
///
/// <para><b>Mixing variance.</b> τ is the prior standard deviation on the preference rate's
/// departure from ½. The test is most powerful where the mixture puts its mass, so τ = 0.15 aims it
/// at the 65/35 preference the review panel named as the effect worth detecting. τ is not a knob to
/// turn after looking at the data: moving it mid-experiment changes what the p-value means, which
/// is why it is configuration and is reported beside the number it produced.</para>
///
/// <para>Reference: Johari, Koomen, Pekelis &amp; Walsh, "Always Valid Inference: Continuous
/// Monitoring of A/B Tests" (KDD 2017 / Operations Research 2022).</para>
/// </summary>
public static class Msprt
{
    /// <summary>The Bernoulli(½) variance the supermartingale is built on.</summary>
    public const double NullVariance = 0.25d;

    /// <summary>
    /// log Λ_n for <paramref name="winsA"/> A-wins out of <paramref name="n"/> decided slates,
    /// against the hypothesis that A's true preference rate is <c>½ + theta0</c>.
    /// <para><paramref name="winsA"/> is a double rather than an int only so the power projection
    /// in <see cref="SlatesToDecision"/> can walk the expected path; real data passes a whole
    /// number.</para>
    /// </summary>
    public static double LogLambda(int n, double winsA, double tau, double theta0 = 0d)
    {
        if (n <= 0)
        {
            return 0d;
        }

        // s is the mixing scale on λ, expressed so the formula reads in terms of the preference
        // scale the caller thinks in: s = τ/σ².
        var s2 = tau * tau / (NullVariance * NullVariance);
        var z = winsA - (n * (0.5d + theta0));
        var d = 1d + (n * NullVariance * s2);
        return (-0.5d * Math.Log(d)) + (z * z * s2 / (2d * d));
    }

    /// <summary>
    /// The always-valid p-value after a stream of preferences: the running minimum of 1/Λ over
    /// every prefix, capped at 1.
    ///
    /// <para>The running minimum is the whole point. A p-value read only at the current n would be
    /// valid only if the founder had committed in advance to reading it exactly then; taking the
    /// minimum over every prefix reports the smallest value the evidence ever reached, which is the
    /// quantity Ville's inequality actually bounds. It is also monotone, so the number on the panel
    /// can never go back up and un-decide a decision the founder already acted on.</para>
    /// </summary>
    public static double AlwaysValidP(IReadOnlyList<bool> aWins, double tau)
    {
        var best = 0d;
        var wins = 0;
        for (var i = 0; i < aWins.Count; i++)
        {
            if (aWins[i])
            {
                wins++;
            }

            var log = LogLambda(i + 1, wins, tau);
            if (log > best)
            {
                best = log;
            }
        }

        return Math.Min(1d, Math.Exp(-best));
    }

    /// <summary>
    /// Half-width of the always-valid confidence interval on A's preference rate, at level
    /// <paramref name="alpha"/>. The interval is the set of rates the sequence has not rejected, so
    /// it is valid at every n simultaneously — it narrows as evidence arrives and never has to be
    /// re-derived for the moment it is read. Returns 0.5 (the whole interval, i.e. no information)
    /// when there are no decided slates.
    /// </summary>
    public static double CiHalfWidth(int n, double tau, double alpha)
    {
        if (n <= 0)
        {
            return 0.5d;
        }

        var s2 = tau * tau / (NullVariance * NullVariance);
        var d = 1d + (n * NullVariance * s2);
        var bound = Math.Log(1d / alpha) + (0.5d * Math.Log(d));
        return Math.Sqrt(2d * d * bound / (n * (double)n * s2));
    }

    /// <summary>
    /// How many decided slates the test needs to cross <paramref name="alpha"/> if A's preference
    /// rate holds at <paramref name="winRate"/> — walking the expected path, so it is the honest
    /// central estimate rather than a best case. Returns <paramref name="cap"/> when the rate is so
    /// close to ½ that the sequence does not decide within the cap; the caller reports that as "not
    /// within the horizon" rather than as a number.
    /// </summary>
    public static int SlatesToDecision(double winRate, double tau, double alpha, int cap = 100_000)
    {
        if (winRate is <= 0d or >= 1d)
        {
            winRate = Math.Clamp(winRate, 1e-6d, 1d - 1e-6d);
        }

        var threshold = Math.Log(1d / alpha);
        for (var n = 1; n <= cap; n++)
        {
            if (LogLambda(n, n * winRate, tau) >= threshold)
            {
                return n;
            }
        }

        return cap;
    }
}

/// <summary>
/// The exact two-sided binomial sign test at p = ½ — the sanity companion to
/// <see cref="Msprt"/>.
///
/// <para>It is here because the two answer different questions and disagreement between them is
/// informative. The sign test is a FIXED-SAMPLE test: it is only honest if you decided in advance
/// to look at exactly this n, so it will always read smaller than the always-valid p, and the gap
/// between them is precisely the price of being allowed to peek. If the always-valid p is
/// significant while the sign test is not, something is wrong with the mSPRT wiring; if the sign
/// test is significant and the always-valid p is not, that is normal and the always-valid one wins.
/// Never quote it as the decision.</para>
/// </summary>
public static class SignTest
{
    /// <summary>Exact two-sided p-value for <paramref name="winsA"/> of <paramref name="n"/> at p = ½.</summary>
    public static double TwoSidedP(int n, int winsA)
    {
        if (n <= 0)
        {
            return 1d;
        }

        var extreme = Math.Max(winsA, n - winsA);
        var tail = 0d;
        for (var k = extreme; k <= n; k++)
        {
            tail += Pmf(n, k);
        }

        return Math.Min(1d, 2d * tail);
    }

    /// <summary>Binomial(n, ½) probability mass at k, computed through log-gamma so large n cannot overflow.</summary>
    private static double Pmf(int n, int k)
    {
        var logC = LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);
        return Math.Exp(logC + (n * Math.Log(0.5d)));
    }

    /// <summary>Lanczos log-gamma. Accurate to ~1e-13 over the range this test ever sees.</summary>
    private static double LogGamma(double x)
    {
        double[] c =
        [
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        ];
        var y = x;
        var tmp = x + 5.5d;
        tmp -= (x + 0.5d) * Math.Log(tmp);
        var ser = 1.000000000190015d;
        for (var j = 0; j < 6; j++)
        {
            ser += c[j] / ++y;
        }

        return -tmp + Math.Log(2.5066282746310005d * ser / x);
    }
}
