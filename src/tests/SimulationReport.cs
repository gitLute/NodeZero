using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NodeZero.Core;

namespace NodeZero.Tests;

/// <summary>Параметры текстового отчёта.</summary>
public sealed class SimulationReportOptions
{
    /// <summary>Сколько суток прогнать.</summary>
    public int Days { get; set; } = 720;

    /// <summary>Как часто печатать строку сводки.</summary>
    public int SampleDays { get; set; } = 30;

    /// <summary>
    /// Пошаговая трассировка первых суток. Заодно отключает автоматическое
    /// фиксирование поражения, иначе трассировка оборвалась бы на том дне,
    /// когда патоген временно ушёл в ноль.
    /// </summary>
    public bool Trace { get; set; }

    /// <summary>Сколько суток печатать в режиме трассировки.</summary>
    public int TraceDays { get; set; } = 240;

    /// <summary>Расписание мутаций для отчёта: (день, мутация).</summary>
    public (int Day, MutationId Mutation)[] Script { get; set; } = Array.Empty<(int, MutationId)>();
}

/// <summary>
/// Текстовый отчёт по прогону симуляции. Нужен для проверки баланса и для того,
/// чтобы быстро увидеть, как ведёт себя патоген, без запуска окна игры.
/// </summary>
/// <summary>
/// Готовые сценарии мутаций для проверки баланса.
/// </summary>
/// <remarks>
/// Нужны, чтобы ответить на вопрос «достижима ли победа», не собирая сценарий вручную.
/// Каждая стратегия отражает разное понимание баланса: скорость против заметности,
/// заметность против смертности, тишина против заразности.
/// </remarks>
public static class Strategies
{
    /// <summary>Сценарий по имени. Неизвестное имя — пустой сценарий (пассивная игра).</summary>
    public static (int Day, MutationId Mutation)[] ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "spread" => new[]
        {
            (25, MutationId.Infectivity),
            (55, MutationId.Water),
            (85, MutationId.Infectivity),
            (120, MutationId.Surface),
            (150, MutationId.Infectivity),
            (185, MutationId.Insects),
            (215, MutationId.Durability),
            (245, MutationId.Infectivity),
            (275, MutationId.Animals),
            (305, MutationId.Infectivity),
            (335, MutationId.Durability),
            (370, MutationId.Infectivity),
        },

        "flame" => new[]
        {
            (20, MutationId.Water),
            (45, MutationId.Infectivity),
            (70, MutationId.Surface),
            (95, MutationId.Infectivity),
            (120, MutationId.ResistCold),
            (145, MutationId.Insects),
            (170, MutationId.Infectivity),
            (200, MutationId.ResistHeat),
            (230, MutationId.Durability),
            (260, MutationId.Infectivity),
            (290, MutationId.Animals),
            (320, MutationId.ResistHumidity),
            (350, MutationId.Infectivity),
            (385, MutationId.ResistDry),
            (420, MutationId.Infectivity),
            (455, MutationId.Incubation),
            (490, MutationId.Infectivity),
            (525, MutationId.Infectivity),
        },

        "stealth" => new[]
        {
            (25, MutationId.Incubation),
            (60, MutationId.SymptomaticSpread),
            (95, MutationId.Incubation),
            (130, MutationId.Water),
            (165, MutationId.SymptomaticSpread),
            (200, MutationId.Insects),
            (240, MutationId.Infectivity),
            (280, MutationId.SymptomaticSpread),
            (320, MutationId.Infectivity),
            (360, MutationId.ResistHumidity),
            (400, MutationId.Infectivity),
            (440, MutationId.ResistHeat),
        },

        "brutal" => new[]
        {
            (25, MutationId.Severity),
            (60, MutationId.Infectivity),
            (100, MutationId.Severity),
            (140, MutationId.Surface),
            (180, MutationId.Severity),
            (215, MutationId.MedicineResistance),
            (250, MutationId.Severity),
            (285, MutationId.Infectivity),
            (320, MutationId.Severity),
            (355, MutationId.MedicineResistance),
            (390, MutationId.Severity),
            (425, MutationId.Infectivity),
        },

        _ => Array.Empty<(int, MutationId)>(),
    };

    /// <summary>Все имена стратегий для справки.</summary>
    public static readonly string[] Names = { "passive", "spread", "flame", "stealth", "brutal" };
}

public static class SimulationReport
{
    public static void Run(TextWriter output, SimulationConfig config, SimulationReportOptions options)
    {
        WorldMap map = WorldGenerator.Generate(config);
        var pathogen = new Pathogen(config);
        var engine = new SimulationEngine(map, pathogen, config);

        int start = engine.SuggestPatientZero();
        HexCell startCell = map.Cells[start];

        if (options.Trace)
        {
            config.ExtinctionGraceDays = int.MaxValue;
        }

        if (options.Script.Length > 0)
        {
            pathogen.EarnDna(MutationCatalog.TotalDna);
        }

        engine.StartOutbreak(start);

        output.WriteLine(WorldGenerator.Describe(map));
        output.WriteLine();

        if (options.Trace)
        {
            output.WriteLine(DescribeAdjacency(map));
            output.WriteLine();
        }

        output.WriteLine(
            $"Нулевой пациент: {startCell.Coord} — {ClimateData.Name(startCell.Climate)}, " +
            $"население {WorldGenerator.FormatPopulation(startCell.Population)}, " +
            $"медицина {startCell.Healthcare * 100.0:0.#}%");
        output.WriteLine(
            $"Контактный вес региона старта: {engine.RelativeContactWeight(start):0.000} " +
            "(выше единицы — патоген здесь распространяется легче среднемирового)");
        output.WriteLine(
            $"R0 патогена: {pathogen.BasicReproduction:0.00}, " +
            $"бета в стартовом регионе: {pathogen.BasicReproduction / pathogen.InfectiousDays:0.000}/сутки, " +
            $"gamma: {pathogen.Gamma:0.000}/сутки, sigma: {pathogen.Sigma:0.000}/сутки");
        output.WriteLine($"Полный бюджет ДНК: {MutationCatalog.TotalDna}, " +
                         $"доступно за партию: {MutationCatalog.TotalDna * config.DnaEarnCap:0.#}");

        if (options.Trace)
        {
            RunTrace(output, engine, map, startCell, options);
            return;
        }

        output.WriteLine();
        output.WriteLine("День   Год    Инфекц.      Умерло      Переболели  R_t      ДНК  Иссл.  Лекарство");
        output.WriteLine(new string('-', 96));

        int scriptCursor = 0;
        int nextSample = 0;

        while (engine.Day < options.Days && engine.Status == GameStatus.Running)
        {
            if (engine.Day >= nextSample)
            {
                WriteSummaryRow(output, engine, (int)engine.Day);
                nextSample += options.SampleDays;
            }

            while (scriptCursor < options.Script.Length && options.Script[scriptCursor].Day <= engine.Day)
            {
                if (engine.TryMutate(options.Script[scriptCursor].Mutation, out string? refusal))
                {
                    MutationDef def = MutationCatalog.Get(options.Script[scriptCursor].Mutation);
                    output.WriteLine(
                        $"      -> мутация «{def.Name}» -> ур. {pathogen.Level(def.Id)}");
                }
                else if (refusal is not null)
                {
                    output.WriteLine($"      -> отказ: {refusal}");
                }

                scriptCursor++;
            }

            engine.AdvanceDays(Math.Min(5.0, options.Days - engine.Day));
        }

        WriteSummaryRow(output, engine, (int)engine.Day);
        WriteEpilogue(output, engine, map);
    }

    private static string DescribeAdjacency(WorldMap map)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Смежность (первые пять наземных регионов):");

        int shown = 0;
        foreach (int globalIndex in map.LandIndices)
        {
            if (shown++ >= 5)
            {
                break;
            }

            HexCell cell = map.Cells[globalIndex];
            builder.Append("  ").Append(cell.Coord).Append(" (глоб. ").Append(globalIndex).Append("): ");
            foreach (int neighborGlobal in cell.Neighbors)
            {
                builder.Append(map.Cells[neighborGlobal].Coord).Append(' ');
            }

            builder.AppendLine();
        }

        builder.Append("  IndexByCoord[(-11,7)] = ")
            .Append(map.IndexByCoord.TryGetValue(new HexCoord(-11, 7), out int probe) ? probe.ToString() : "нет")
            .AppendLine();
        return builder.ToString().TrimEnd();
    }

    private static void RunTrace(TextWriter output, SimulationEngine engine, WorldMap map, HexCell cell, SimulationReportOptions options)
    {
        output.WriteLine();
        output.WriteLine(
            $"Трассировка. Регион старта {cell.Coord}: население " +
            $"{WorldGenerator.FormatPopulation(cell.Population)}, " +
            $"климат {ClimateData.Name(cell.Climate)}, медицина {cell.Healthcare:0.00}, " +
            $"контактный вес {engine.RelativeContactWeight(cell.Index):0.000}");
        output.WriteLine($"Граф контактов: {engine.ContactDiagnostics(cell.Index)}");
        output.WriteLine($"Передача:      {engine.BetaDiagnostics(cell.Index)}");
        output.WriteLine();
        output.WriteLine(
            "День  Регион:    S         E         I         R         D   |  Мир: зараж.  охват.  R_t");
        output.WriteLine(new string('-', 104));

        for (int day = 1; day <= options.TraceDays && engine.Status == GameStatus.Running; day++)
        {
            engine.AdvanceDays(1.0);

            int covered = 0;
            foreach (int index in map.LandIndices)
            {
                HexCell other = map.Cells[index];
                if (other.Infectious + other.Exposed > 0.0)
                {
                    covered++;
                }
            }

            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,5} {1,10:0.###E+0} {2,9:0.###} {3,9:0.###} {4,9:0.###} {5,9:0.###}   | {6,10:0.###} {7,7:0} {8,6:0.00}",
                day,
                cell.Susceptible,
                cell.Exposed,
                cell.Infectious,
                cell.Recovered,
                cell.Dead,
                engine.TotalInfectious,
                covered,
                engine.InstantaneousReproduction));
        }

        WriteEpilogue(output, engine, map);
    }

    private static void WriteSummaryRow(TextWriter output, SimulationEngine engine, int day)
    {
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0,5} {1,6:0.00} {2,11:0.###E+0} {3,11:0.###E+0} {4,11:0.###E+0} {5,7:0.00} {6,5:0} {7,6:0} {8,7:0}%",
            day,
            engine.Years,
            engine.TotalInfectious,
            engine.TotalDead,
            engine.TotalRecovered,
            engine.InstantaneousReproduction,
            engine.Pathogen.AvailableDna,
            engine.Research.Progress,
            engine.Research.CureEffectiveness * 100.0));
    }

    private static void WriteEpilogue(TextWriter output, SimulationEngine engine, WorldMap map)
    {
        output.WriteLine();
        output.WriteLine($"Итоговое состояние: {engine.Status}, день {engine.Day:0} ({engine.Years:0.0} лет)");
        output.WriteLine(
            $"Переболевшие за партию: {engine.InfectedEverShare * 100.0:0.##}%, " +
            $"умерло {engine.TotalDead / Math.Max(1e-9, map.TotalPopulation) * 100.0:0.##}%");
        output.WriteLine($"Баланс массы (относительная ошибка): {map.MassBalanceError():0.##E+0}");
        output.WriteLine(
            $"Иммунитет: срок {engine.Pathogen.ImmunityDays:0} сут, потеряли {engine.TotalLostImmunity:0.###} чел.",
            $"Исследования: прогресс {engine.Research.Progress:0.0}, стадия {engine.Research.Stage}, " +
            $"лекарство {engine.Research.CureEffectiveness * 100.0:0.#}%, " +
            $"идентификация {(engine.Research.Identified ? "да" : "нет")}, " +
            $"порог идентификации {engine.Research.IdentificationThreshold / map.TotalPopulation * 100.0:0.###}%");
        output.WriteLine();
        output.WriteLine("Новостная лента:");
        foreach (GameEvent e in engine.Events.TakeLast(20))
        {
            output.WriteLine($"  день {e.Day,5}  [{e.Kind,-10}] {e.Text}");
        }
    }
}