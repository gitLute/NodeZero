using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>
/// Детерминированный шум с плавной интерполяцией. Один и тот же сид всегда даёт
/// одну и ту же карту: вся процедурная генерация мира строится на нём.
/// </summary>
public sealed class ValueNoise
{
    private readonly ulong _seed;
    private readonly Dictionary<long, double> _cache = new();

    public ValueNoise(ulong seed)
    {
        _seed = seed;
    }

    /// <summary>Шестидесятичетырёхбитное перемешивание (SplitMix64).</summary>
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Псевдослучайное значение узла решётки в [0, 1).</summary>
    private double Corner(int x, int y)
    {
        long key = ((long)x << 32) ^ (uint)y;
        if (_cache.TryGetValue(key, out double cached))
        {
            return cached;
        }

        ulong h = Mix((ulong)x * 73856093UL ^ (ulong)y * 19349663UL ^ _seed);
        double value = (h >> 11) * (1.0 / 9007199254740992.0);
        _cache[key] = value;
        return value;
    }

    /// <summary>Значение шума в точке с билинейной интерполяцией по четырём углам ячейки.</summary>
    public double At(double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;

        // Сглаживание Hermite, чтобы шум не имел квадратных градиентных разрывов.
        double u = fx * fx * (3.0 - 2.0 * fx);
        double v = fy * fy * (3.0 - 2.0 * fy);

        double n00 = Corner(x0, y0);
        double n10 = Corner(x0 + 1, y0);
        double n01 = Corner(x0, y0 + 1);
        double n11 = Corner(x0 + 1, y0 + 1);

        double top = n00 + (n10 - n00) * u;
        double bottom = n01 + (n11 - n01) * u;
        return top + (bottom - top) * v;
    }

    /// <summary>
    /// Фрактальный шум: несколько октав с уменьшающейся амплитудой и растущей частотой.
    /// Даёт континенты и естественную «рваность» береговой линии.
    /// </summary>
    public double Fbm(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0.0;
        double amplitude = 1.0;
        double frequency = 1.0;
        double total = 0.0;

        for (int i = 0; i < octaves; i++)
        {
            sum += amplitude * At(x * frequency, y * frequency);
            total += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return total > 0.0 ? sum / total : 0.0;
    }
}