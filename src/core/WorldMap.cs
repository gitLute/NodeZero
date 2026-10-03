using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>
/// Сгенерированный мир: диск гексов, часть из которых — суша с населением.
/// Сетка регулярная (включая океан), поэтому у каждого гекса есть шесть соседей,
/// и соседство считается без специальных случаев на краях карты.
/// </summary>
public sealed class WorldMap
{
    public int Radius { get; }

    public HexCoord Center { get; }

    public HexCell[] Cells { get; }

    public Dictionary<HexCoord, int> IndexByCoord { get; }

    /// <summary>Индексы всех наземных регионов.</summary>
    public int[] LandIndices { get; }

    public double TotalPopulation { get; }

    public ulong Seed { get; }

    public WorldMap(int radius, ulong seed, HexCell[] cells, Dictionary<HexCoord, int> indexByCoord)
    {
        Radius = radius;
        Seed = seed;
        Center = new HexCoord(0, 0);
        Cells = cells;
        IndexByCoord = indexByCoord;

        var land = new List<int>(cells.Length);
        double total = 0.0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].IsLand)
            {
                land.Add(i);
                total += cells[i].Population;
            }
        }

        LandIndices = land.ToArray();
        TotalPopulation = total;
    }

    public HexCell this[int index] => Cells[index];

    public HexCoord CoordOf(int index) => Cells[index].Coord;

    /// <summary>
    /// Материальный баланс: сумма компартментальных величин должна совпадать
    /// с исходным населением в пределах накопленной погрешности округления.
    /// </summary>
    public double MassBalanceError()
    {
        double expected = TotalPopulation;
        double actual = 0.0;
        for (int i = 0; i < Cells.Length; i++)
        {
            actual += Cells[i].TotalCompartments;
        }

        return expected > 0.0 ? Math.Abs(actual - expected) / expected : 0.0;
    }

    /// <summary>
    /// Проверка связности суши: из любого наземного региона должен быть путь
    /// в любой другой только по суше. Иначе патоген застрянет на одном острове.
    /// </summary>
    public bool IsLandConnected()
    {
        if (LandIndices.Length == 0)
        {
            return false;
        }

        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(LandIndices[0]);
        visited.Add(LandIndices[0]);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in Cells[current].Neighbors)
            {
                if (!Cells[neighbor].IsLand || !visited.Add(neighbor))
                {
                    continue;
                }

                queue.Enqueue(neighbor);
            }
        }

        return visited.Count == LandIndices.Length;
    }

    /// <summary>Хеш статической части мира. Одинаковый сид — одинаковый хеш.</summary>
    public ulong StructuralHash()
    {
        ulong hash = 14695981039346656037UL;
        hash = MixInt(hash, Radius);
        hash = MixInt(hash, Cells.Length);
        for (int i = 0; i < Cells.Length; i++)
        {
            HexCell cell = Cells[i];
            hash = MixInt(hash, cell.IsLand ? 1 : 0);
            hash = MixInt(hash, cell.Coord.Q);
            hash = MixInt(hash, cell.Coord.R);
            hash = MixDouble(hash, cell.Population);
            hash = MixDouble(hash, cell.Healthcare);
            hash = MixDouble(hash, cell.Urbanization);
            hash = MixDouble(hash, cell.WaterAvailability);
            hash = MixDouble(hash, cell.VectorHabitat);
            hash = MixDouble(hash, cell.Livestock);
        }

        return hash;
    }

    private static ulong MixInt(ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        hash *= 1099511628211UL;
        return hash;
    }

    private static ulong MixDouble(ulong hash, double value)
    {
        hash ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        hash *= 1099511628211UL;
        return hash;
    }
}