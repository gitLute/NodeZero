namespace NodeZero.Core;

/// <summary>
/// Детерминированный генератор псевдослучайных чисел (PCG-XSH-RR 64/32).
/// </summary>
/// <remarks>
/// Требование проекта «детерминированная эволюция» означает, что один и тот же
/// сид должен давать побайтово одинаковую карту и одинаковый ход симуляции.
/// <see cref="System.Random"/> для этого не годится: его алгоритм менялся между
/// версиями .NET, поэтому зафиксированный сид не гарантирует воспроизводимость.
/// </remarks>
public sealed class DeterministicRandom
{
    private const ulong Multiplier = 6364136223846793005UL;
    private const ulong Increment = 1442695040888963407UL;

    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        _state = 0UL;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    /// <summary>Следующее 32-битное значение.</summary>
    public uint NextUInt()
    {
        ulong old = _state;
        _state = old * Multiplier + Increment;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Равномерное значение в [0, 1).</summary>
    public double NextDouble() => (NextUInt() >> 8) * (1.0 / 16777216.0);

    public double Range(double min, double max) => min + NextDouble() * (max - min);

    public int RangeInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        return minInclusive + (int)(NextUInt() % (uint)(maxExclusive - minInclusive));
    }

    public bool Chance(double probability) => NextDouble() < probability;
}