using System;
using System.Collections.Generic;
using System.Text;

namespace Content.Server.Intentions.Waves;

/// <summary>
/// Provides deterministic random choices for one distribution wave.
/// </summary>
public sealed class IntentionsDeterministicRandom
{
    private uint _state;

    /// <summary>
    /// Initializes the deterministic random generator with an explicit seed.
    /// </summary>
    public IntentionsDeterministicRandom(int seed)
    {
        Seed = seed;
        _state = unchecked((uint) seed);
    }

    /// <summary>
    /// Gets the seed currently driving the deterministic sequence.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Returns the next deterministic integer in the range <c>[0, maxExclusive)</c>.
    /// </summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Max exclusive must be greater than zero.");

        return (int) (NextUInt() % (uint) maxExclusive);
    }

    /// <summary>
    /// Picks one item using deterministic weighted random selection.
    /// </summary>
    public T PickWeighted<T>(IReadOnlyList<T> items, Func<T, int> weightSelector)
    {
        if (items.Count == 0)
            throw new ArgumentException("Cannot pick from an empty list.", nameof(items));

        var totalWeight = 0;
        foreach (var item in items)
            totalWeight += Math.Max(0, weightSelector(item));

        if (totalWeight <= 0)
            throw new ArgumentException("At least one item must have a positive weight.", nameof(items));

        var roll = Next(totalWeight);
        var cumulative = 0;

        foreach (var item in items)
        {
            cumulative += Math.Max(0, weightSelector(item));
            if (roll < cumulative)
                return item;
        }

        return items[^1];
    }

    /// <summary>
    /// Builds a stable hash-based seed from wave metadata without using platform-specific string hashing.
    /// </summary>
    public static int BuildSeed(int waveId, string snapshotId, string gameMode, long stationTimeTicks)
    {
        var hash = 2166136261u;
        Add(ref hash, waveId.ToString());
        Add(ref hash, snapshotId);
        Add(ref hash, gameMode);
        Add(ref hash, stationTimeTicks.ToString());

        return unchecked((int) hash);
    }

    /// <summary>
    /// Advances the internal linear congruential generator.
    /// </summary>
    private uint NextUInt()
    {
        _state = unchecked(_state * 1664525u + 1013904223u);
        return _state;
    }

    /// <summary>
    /// Mixes one string fragment into the running seed hash.
    /// </summary>
    private static void Add(ref uint hash, string value)
    {
        foreach (var item in Encoding.UTF8.GetBytes(value))
        {
            hash ^= item;
            hash *= 16777619u;
        }

        hash ^= 0xff;
        hash *= 16777619u;
    }
}
