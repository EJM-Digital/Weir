using System.Globalization;
using FsCheck;
using FsCheck.Fluent;

namespace Weir.TestSupport;

/// <summary>
/// Runs a property over generated inputs with a seed that is the same on every run, so a pass or a failure can be
/// repeated exactly. A failure names the seed and the shrunk counterexample; set <c>WEIR_PROPERTY_SEED</c> to that
/// number to run the same inputs again, or to <c>random</c> to look for new ones (the seed it picked is in the failure).
/// </summary>
/// <remarks>
/// This file is compiled into each test project that has property tests (it is linked, not shared as an assembly).
/// </remarks>
internal static class PropertyChecks
{
    /// <summary>The seed every run uses unless <c>WEIR_PROPERTY_SEED</c> says otherwise.</summary>
    public const ulong FixedSeed = 20_261_015;

    /// <summary>How many inputs a property is tried on unless a test asks for another number.</summary>
    public const int DefaultRuns = 100;

    private const string SeedVariable = "WEIR_PROPERTY_SEED";
    private const string RunsVariable = "WEIR_PROPERTY_RUNS";

    /// <summary>Tries <paramref name="property"/> on inputs from <paramref name="arbitrary"/>, shrinking a failure to the smallest input.</summary>
    public static void ForAll<T>(Arbitrary<T> arbitrary, Action<T> property, int runs = DefaultRuns)
    {
        ArgumentNullException.ThrowIfNull(arbitrary);
        ArgumentNullException.ThrowIfNull(property);
        var seed = ChosenSeed();
        var configuration = Config.QuickThrowOnFailure
            .WithMaxTest(ChosenRuns(runs))
            .WithReplay(new Replay(new Rnd(seed), null));
        try
        {
            Check.One(configuration, Prop.ForAll(arbitrary, property));
        }
        catch (Exception exception)
        {
            throw new PropertyFailedException(seed, exception);
        }
    }

    /// <summary>Tries <paramref name="property"/> on inputs from <paramref name="generator"/>. A failure is not shrunk.</summary>
    public static void ForAll<T>(Gen<T> generator, Action<T> property, int runs = DefaultRuns) =>
        ForAll(Arb.From(generator), property, runs);

    /// <summary>Tries a property of two inputs that are drawn independently.</summary>
    public static void ForAll<T1, T2>(Gen<T1> first, Gen<T2> second, Action<T1, T2> property, int runs = DefaultRuns) =>
        ForAll(first.Zip(second), pair => property(pair.Item1, pair.Item2), runs);

    /// <summary>
    /// Inputs from <paramref name="generator"/> for a test that has to wait between them (it sends each to a running server), the same on every run
    /// for a given <paramref name="seed"/>. A failing test names the seed and the input.
    /// </summary>
    public static IReadOnlyList<T> Samples<T>(Gen<T> generator, int count, out ulong seed)
    {
        ArgumentNullException.ThrowIfNull(generator);
        seed = ChosenSeed();
        return [.. generator.Sample(50, new Rnd(seed), count)];
    }

    private static ulong ChosenSeed()
    {
        var text = Environment.GetEnvironmentVariable(SeedVariable)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return FixedSeed;
        }

        if (text.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            return (ulong)Random.Shared.NextInt64(1, uint.MaxValue);
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seed)
            ? seed
            : throw new InvalidOperationException($"{SeedVariable} must be a whole number or 'random', not '{text}'.");
    }

    private static int ChosenRuns(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(RunsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var runs) && runs > 0
            ? runs
            : fallback;
}

/// <summary>A property that failed, with the seed that reproduces it.</summary>
internal sealed class PropertyFailedException : Exception
{
    public PropertyFailedException(ulong seed, Exception inner)
        : base(
            $"Property failed with seed {seed.ToString(CultureInfo.InvariantCulture)}. " +
            $"Run it again with WEIR_PROPERTY_SEED={seed.ToString(CultureInfo.InvariantCulture)}.{Environment.NewLine}{inner.Message}",
            inner)
    {
    }
}
