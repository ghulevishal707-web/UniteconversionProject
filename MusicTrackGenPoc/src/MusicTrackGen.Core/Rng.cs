using System.Security.Cryptography;
using System.Text;

namespace MusicTrackGen.Core;

/// <summary>
/// Deterministic xoshiro256** generator. Every random decision in a generated
/// track draws from one of these, seeded from the track seed, so the same seed
/// always reproduces the same song bit-for-bit.
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public ulong Seed { get; }

    public Rng(ulong seed)
    {
        Seed = seed;
        // SplitMix64 to spread a single seed across the 256-bit state.
        ulong z = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        _s0 = SplitMix(ref z);
        _s1 = SplitMix(ref z);
        _s2 = SplitMix(ref z);
        _s3 = SplitMix(ref z);
    }

    /// <summary>A child generator for an independent decision stream (still seed-derived).</summary>
    public Rng Fork(string label) => new(Hash64($"{Seed}:{label}"));

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0,1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform in [min,max).</summary>
    public double Range(double min, double max) => min + NextDouble() * (max - min);

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + (int)(NextDouble() * (maxExclusive - minInclusive));
    }

    public bool Chance(double p) => NextDouble() < p;

    /// <summary>Box-Muller normal sample, clamped to +/-3 sigma.</summary>
    public double Gaussian(double mean, double sd)
    {
        double u1 = Math.Max(1e-12, NextDouble());
        double u2 = NextDouble();
        double n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + sd * Math.Clamp(n, -3.0, 3.0);
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(0, items.Count)];

    /// <summary>Weighted pick; weights need not be normalised.</summary>
    public int PickWeighted(IReadOnlyList<double> weights)
    {
        double total = 0;
        for (int i = 0; i < weights.Count; i++) total += Math.Max(0, weights[i]);
        if (total <= 0) return Next(0, weights.Count);
        double r = NextDouble() * total;
        for (int i = 0; i < weights.Count; i++)
        {
            r -= Math.Max(0, weights[i]);
            if (r <= 0) return i;
        }
        return weights.Count - 1;
    }

    public static ulong Hash64(string text)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToUInt64(h, 0);
    }

    /// <summary>A fresh non-reproducible seed for "surprise me" generation.</summary>
    public static ulong NewSeed()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return BitConverter.ToUInt64(b);
    }
}
