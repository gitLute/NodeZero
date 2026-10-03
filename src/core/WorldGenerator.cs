using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>
/// Процедурная генерация мира. Полностью детерминирована: один сид — одна карта.
///
/// Идея генерации в том, что все свойства региона выводятся из четырёх независимых
/// полей шума (рельеф, температура, влажность, урбанизация) плюс расстояния от
/// центра карты, которое играет роль широты. Поэтому тропики лежат в центре,
/// а полярные области — по краям диска.
/// </summary>
public static class WorldGenerator
{
    /// <summary>Максимальная доля населения мира, которую может занимать один регион.</summary>
    private const double MaxCellShare = 0.085;

    public static WorldMap Generate(SimulationConfig config)
    {
        var noise = new ValueNoise(config.Seed);
        int radius = config.MapRadius;
        var center = new HexCoord(0, 0);

        var coordList = new List<HexCoord>(HexCoord.Disc(center, radius));
        var cells = new HexCell[coordList.Count];
        var indexByCoord = new Dictionary<HexCoord, int>(coordList.Count);

        var weights = new double[coordList.Count];
        double weightSum = 0.0;

        for (int i = 0; i < coordList.Count; i++)
        {
            HexCoord coord = coordList[i];
            indexByCoord[coord] = i;

            // Расстояние от центра карты играет роль широты. Показатель степени
            // подбирает так, чтобы суша (она и так сосредоточена ближе к центру)
            // в основном попадала в умеренные и тропические пояса, а полярные
            // области были узкой каймой на краю.
            double latitude = radius > 0 ? coord.DistanceTo(center) / (double)radius : 0.0;
            latitude = Math.Pow(latitude, 2.0);

            // Рельеф: фрактальный шум плюс радиальный спад к краю диска,
            // чтобы суша имела береговую линию, а карта не уходила в бесконечный океан.
            double relief = noise.Fbm(coord.Q * 0.16, coord.R * 0.16, 5);
            double falloff = Math.Pow(1.0 - latitude, 0.7);
            double landScore = relief * 1.30 + falloff * 1.05 - 1.16;
            bool isLand = landScore > 0.0;

            var cell = new HexCell
            {
                Index = i,
                Coord = coord,
                IsLand = isLand,
                LandQuality = isLand ? Math.Clamp(landScore * 2.4, 0.12, 1.0) : 0.0,
            };

            if (!isLand)
            {
                cells[i] = cell;
                weights[i] = 0.0;
                continue;
            }

            // Температура: полюс у края карты, тропики в центре, с шумовыми отклонениями.
            double temperatureNoise = noise.Fbm(coord.Q * 0.11 + 31.7, coord.R * 0.11 - 12.3, 4);
            double temperature = Math.Clamp(
                0.95 - 1.30 * latitude
                + (relief - 0.5) * 0.50
                + (temperatureNoise - 0.5) * 0.45,
                0.0, 1.0);

            double humidityNoise = noise.Fbm(coord.Q * 0.13 - 55.1, coord.R * 0.13 + 77.9, 4);
            double humidity = Math.Clamp(
                0.5 + (humidityNoise - 0.5) * 1.2 + (temperature - 0.5) * 0.55,
                0.0, 1.0);

            cell.Climate = ClassifyClimate(temperature, humidity);

            // Урбанизация: два поля — плотная застройка и редкие мегаполисы.
            double urbanNoise = noise.Fbm(coord.Q * 0.28 + 7.7, coord.R * 0.28 + 3.3, 4, 2.0, 0.55);
            double metropolisNoise = noise.At(coord.Q * 0.09 - 5.5, coord.R * 0.09 + 11.1);
            double urban = Math.Clamp((urbanNoise - 0.34) * 1.9, 0.0, 1.0);
            urban = Math.Pow(urban, 1.5);
            double metropolis = SmoothStep(0.60, 0.86, metropolisNoise);
            cell.Urbanization = Math.Clamp(urban * 0.72 + metropolis * 0.85, 0.0, 1.0);

            double fertilityNoise = noise.Fbm(coord.Q * 0.33 + 100.0, coord.R * 0.33 + 100.0, 4);
            double fertility = 0.32 + 0.95 * fertilityNoise;
            double habitability = ClimateData.Habitability(cell.Climate);

            double wealthNoise = noise.Fbm(coord.Q * 0.19 - 44.2, coord.R * 0.19 + 88.8, 4);
            double wealth = Math.Clamp(0.10 + 0.55 * Math.Pow(wealthNoise, 1.5) + 0.50 * cell.Urbanization, 0.0, 1.0);
            cell.Healthcare = Math.Clamp(wealth * (0.55 + 0.45 * habitability), 0.02, 0.94);

            cell.WaterAvailability = Math.Clamp(0.5 + (humidity - 0.5) * 1.1 + (fertility - 0.5) * 0.5, 0.0, 1.0);
            cell.VectorHabitat = Math.Clamp((humidity - 0.33) * 1.35 + (temperature - 0.38) * 0.85, 0.0, 1.0);

            double livestockNoise = noise.At(coord.Q * 0.26 + 13.7, coord.R * 0.26 - 91.4);
            cell.Livestock = Math.Clamp((0.22 + 1.15 * livestockNoise) * (1.0 - 0.55 * cell.Urbanization), 0.0, 1.0);

            cell.ContactDensity = 0.45 + 1.25 * cell.Urbanization;

            weights[i] = Math.Pow(
                cell.LandQuality * fertility * habitability * (0.20 + 1.60 * cell.Urbanization),
                0.9);
            weightSum += weights[i];

            cells[i] = cell;
        }

        if (weightSum <= 0.0)
        {
            throw new InvalidOperationException(
                "Сгенерированный мир не содержит суши. Смените сид или увеличьте радиус карты.");
        }

        var populations = DistributePopulation(weights, weightSum, config.TargetWorldPopulation);

        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Population = populations[i];
            cells[i].InitializePopulation();
        }

        var map = new WorldMap(radius, config.Seed, cells, indexByCoord);
        BuildAdjacency(map);
        return map;
    }

    /// <summary>Разделение климатического квадранта (температура, влажность) на пять зон.</summary>
    private static Climate ClassifyClimate(double temperature, double humidity)
    {
        if (temperature < 0.20)
        {
            return Climate.Polar;
        }

        if (temperature < 0.45)
        {
            return humidity < 0.45 ? Climate.ColdDry : Climate.ColdHumid;
        }

        return humidity < 0.42 ? Climate.HotDry : Climate.HotHumid;
    }

    /// <summary>
    /// Распределение целевого населения по регионам пропорционально весам,
    /// с «заполнением воды»: слишком крупный регион подрезается до потолка, а
    /// освободившееся население перетекает в остальные. Это не даёт одному гексу
    /// сглотать полмира.
    /// </summary>
    private static double[] DistributePopulation(double[] weights, double weightSum, double targetTotal)
    {
        var result = new double[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            result[i] = weights[i] > 0.0 ? targetTotal * weights[i] / weightSum : 0.0;
        }

        double cap = targetTotal * MaxCellShare;
        var capped = new bool[weights.Length];

        for (int iteration = 0; iteration < 32; iteration++)
        {
            double excess = 0.0;
            double freeWeight = 0.0;
            Array.Clear(capped, 0, capped.Length);

            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0.0)
                {
                    continue;
                }

                if (result[i] > cap)
                {
                    excess += result[i] - cap;
                    result[i] = cap;
                    capped[i] = true;
                }
                else
                {
                    freeWeight += weights[i];
                }
            }

            if (excess <= 0.0 || freeWeight <= 0.0)
            {
                break;
            }

            for (int i = 0; i < weights.Length; i++)
            {
                if (!capped[i] && weights[i] > 0.0)
                {
                    result[i] += excess * weights[i] / freeWeight;
                }
            }
        }

        return result;
    }

    /// <summary>Построение списков соседей: в список попадают только гексы внутри карты.</summary>
    private static void BuildAdjacency(WorldMap map)
    {
        foreach (HexCell cell in map.Cells)
        {
            var neighbors = new List<int>(6);
            for (int direction = 0; direction < HexCoord.Directions.Length; direction++)
            {
                HexCoord neighborCoord = cell.Coord.Neighbor(direction);
                if (map.IndexByCoord.TryGetValue(neighborCoord, out int neighborIndex))
                {
                    neighbors.Add(neighborIndex);
                }
            }

            cell.Neighbors = neighbors.ToArray();
        }
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0.0 : 1.0;
        }

        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Сводка по миру — используется самопроверкой и текстовым отчётом.</summary>
    public static string Describe(WorldMap map)
    {
        int totalCells = map.Cells.Length;
        int landCells = map.LandIndices.Length;
        var perClimate = new int[ClimateData.Count];
        double maxPopulation = 0.0;
        int maxCell = -1;
        double healthcareSum = 0.0;

        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            perClimate[(int)cell.Climate]++;
            healthcareSum += cell.Healthcare;
            if (cell.Population > maxPopulation)
            {
                maxPopulation = cell.Population;
                maxCell = index;
            }
        }

        var climateParts = new List<string>(perClimate.Length);
        for (int i = 0; i < perClimate.Length; i++)
        {
            climateParts.Add($"{ClimateData.Name((Climate)i)}: {perClimate[i]}");
        }

        string climates = string.Join(", ", climateParts);

        double averageHealthcare = landCells > 0 ? healthcareSum / landCells : 0.0;

        return string.Join(
            System.Environment.NewLine,
            $"Радиус карты: {map.Radius}   Гексов всего: {totalCells}   Суша: {landCells} " +
            $"({100.0 * landCells / Math.Max(1, totalCells):0.0}%)",
            $"Население мира: {FormatPopulation(map.TotalPopulation)}   " +
            $"Крупнейший регион: {FormatPopulation(maxPopulation)} {map.CoordOf(maxCell)}   " +
            $"Средний уровень медицины: {averageHealthcare:0.000}",
            $"Климаты — {climates}",
            $"Хеш мира: 0x{map.StructuralHash():X16}");
    }

    /// <summary>Человекочитаемое население: 1.24 млрд вместо 1240000000.</summary>
    public static string FormatPopulation(double value)
    {
        if (value >= 1e9)
        {
            return $"{value / 1e9:0.00} млрд";
        }

        if (value >= 1e6)
        {
            return $"{value / 1e6:0.00} млн";
        }

        if (value >= 1e3)
        {
            return $"{value / 1e3:0.0} тыс";
        }

        return $"{value:0.###}";
    }
}