using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>Аксиальные координаты гексагональной сетки.</summary>
/// <remarks>
/// Используется система pointy-top: соседи задаются шестью смещениями, сумма
/// которых равна нулю. Дистанция считается кубической, то есть по числу
/// переходов между гексами.
/// </remarks>
public readonly struct HexCoord : System.IEquatable<HexCoord>
{
    public readonly int Q;
    public readonly int R;

    public HexCoord(int q, int r)
    {
        Q = q;
        R = r;
    }

    /// <summary>Шесть направлений соседства: начиная с востока, против часовой стрелки.</summary>
    public static readonly HexCoord[] Directions =
    {
        new(1, 0),
        new(1, -1),
        new(0, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, 1),
    };

    /// <summary>Сосед в заданном направлении: собственные координаты плюс смещение.</summary>
    public HexCoord Neighbor(int direction)
    {
        HexCoord delta = Directions[direction];
        return new HexCoord(Q + delta.Q, R + delta.R);
    }

    /// <summary>Кубическая (гексагональная) дистанция: число шагов до соседа.</summary>
    public int DistanceTo(HexCoord other)
    {
        int dq = Q - other.Q;
        int dr = R - other.R;
        return (System.Math.Abs(dq) + System.Math.Abs(dq + dr) + System.Math.Abs(dr)) / 2;
    }

    /// <summary>Кольцо гексов заданного радиуса вокруг центра, включая сам центр при radius = 0.</summary>
    public static IEnumerable<HexCoord> Ring(HexCoord center, int radius)
    {
        if (radius <= 0)
        {
            yield return center;
            yield break;
        }

        HexCoord start = new(
            center.Q + Directions[4].Q * radius,
            center.R + Directions[4].R * radius);

        HexCoord current = start;
        for (int side = 0; side < 6; side++)
        {
            for (int step = 0; step < radius; step++)
            {
                yield return current;
                current = current.Neighbor(side);
            }
        }
    }

    /// <summary>Все гексы внутри диска радиуса radius вместе с центром.</summary>
    public static IEnumerable<HexCoord> Disc(HexCoord center, int radius)
    {
        for (int q = -radius; q <= radius; q++)
        {
            int rLow = System.Math.Max(-radius, -q - radius);
            int rHigh = System.Math.Min(radius, -q + radius);
            for (int r = rLow; r <= rHigh; r++)
            {
                yield return new HexCoord(q, r);
            }
        }
    }

    public bool Equals(HexCoord other) => Q == other.Q && R == other.R;

    public override bool Equals(object? obj) => obj is HexCoord other && Equals(other);

    public override int GetHashCode() => System.HashCode.Combine(Q, R);

    public override string ToString() => $"({Q},{R})";

    public static bool operator ==(HexCoord a, HexCoord b) => a.Equals(b);

    public static bool operator !=(HexCoord a, HexCoord b) => !a.Equals(b);

    /// <summary>Сложение координат: удобно для проверки соседей.</summary>
    public static HexCoord operator +(HexCoord a, HexCoord b) => new(a.Q + b.Q, a.R + b.R);
}