using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>
/// Патоген игрока: набор мутаций и производные от них эпидемиологические величины.
///
/// Ключевая идея: никакой «удачи» здесь нет. Каждая мутация меняет конкретные
/// коэффициенты модели, а результат можно посчитать заранее — это и есть
/// заявленная детерминированная эволюция.
/// </summary>
public sealed class Pathogen
{
    /// <summary>Пары «климат — влияющая на него мутация сопротивления».</summary>
    private static readonly (Climate Climate, MutationId Trait)[] ClimateTraitTable =
    {
        (Climate.Polar, MutationId.ResistCold),
        (Climate.Polar, MutationId.ResistDry),
        (Climate.ColdDry, MutationId.ResistCold),
        (Climate.ColdDry, MutationId.ResistDry),
        (Climate.ColdHumid, MutationId.ResistCold),
        (Climate.ColdHumid, MutationId.ResistHumidity),
        (Climate.HotDry, MutationId.ResistHeat),
        (Climate.HotDry, MutationId.ResistDry),
        (Climate.HotHumid, MutationId.ResistHeat),
        (Climate.HotHumid, MutationId.ResistHumidity),
    };

    private static readonly double InfectivityPerLevel = 1.25;
    private static readonly double SymptomaticSpreadPerLevel = 1.12;
    private static readonly double IncubationPerLevel = 1.25;
    private static readonly double DurabilityPerLevel = 1.20;
    private static readonly double SeverityPerLevel = 1.45;
    private static readonly double SymptomLossPerLevel = 0.08;
    private static readonly double ResistancePerLevel = 0.16;
    private static readonly double MedicineResistancePerLevel = 0.12;

    private readonly Dictionary<MutationId, int> _levels = new();

    public Pathogen(SimulationConfig config)
    {
        Config = config;
        Name = $"Патоген-{config.Seed % 100000:00000}";

        foreach (MutationDef def in MutationCatalog.All)
        {
            // Уровень 0 — исходное состояние. Открытым с рождения является только
            // воздушно-капельный путь передачи.
            _levels[def.Id] = def.StartsUnlocked ? 1 : 0;
        }
    }

    public SimulationConfig Config { get; }

    public string Name { get; set; }

    /// <summary>Всего заработано очков ДНК за партию (с ограничением сверху).</summary>
    public double EarnedDna { get; private set; }

    /// <summary>Потрачено очков ДНК.</summary>
    public double SpentDna { get; private set; }

    public double AvailableDna => EarnedDna - SpentDna;

    /// <summary>Полный бюджет, доступный за партию.</summary>
    public double DnaCap => MutationCatalog.TotalDna * Config.DnaEarnCap;

    public int Level(MutationId id) => _levels.GetValueOrDefault(id);

    public MutationDef Definition(MutationId id) => MutationCatalog.Get(id);

    public bool IsAtMax(MutationId id) => Level(id) >= Definition(id).MaxLevel;

    public int CostToNextLevel(MutationId id) =>
        IsAtMax(id) ? 0 : Definition(id).CostOfLevel(Level(id) + 1);

    /// <summary>Выполнены ли требования по другим мутациям.</summary>
    public bool PrerequisitesMet(MutationId id)
    {
        foreach (MutationRequirement requirement in MutationCatalog.RequirementsOf(id))
        {
            if (Level(requirement.Id) < requirement.Level)
            {
                return false;
            }
        }

        return true;
    }

    public bool CanMutate(MutationId id) =>
        !IsAtMax(id) && PrerequisitesMet(id) && AvailableDna >= CostToNextLevel(id);

    /// <summary>
    /// Покупка следующего уровня мутации. Возвращает false, если мутация
    /// недоступна: не хватает очков ДНК или не выполнены требования.
    /// </summary>
    public bool TryMutate(MutationId id, out string? refusal)
    {
        if (IsAtMax(id))
        {
            refusal = "Достигнут максимальный уровень.";
            return false;
        }

        if (!PrerequisitesMet(id))
        {
            refusal = "Не выполнены требования: " + DescribeRequirements(id);
            return false;
        }

        int cost = CostToNextLevel(id);
        if (AvailableDna < cost)
        {
            refusal = $"Не хватает очков ДНК: нужно {cost}, доступно {AvailableDna:0.#}.";
            return false;
        }

        _levels[id] = Level(id) + 1;
        SpentDna += cost;

        refusal = null;
        return true;
    }

    public string DescribeRequirements(MutationId id)
    {
        MutationRequirement[] requirements = MutationCatalog.RequirementsOf(id);
        if (requirements.Length == 0)
        {
            return "нет";
        }

        var parts = new List<string>(requirements.Length);
        foreach (MutationRequirement requirement in requirements)
        {
            parts.Add($"{MutationCatalog.Get(requirement.Id).Name} ур. {requirement.Level}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Начисление очков ДНК. Общий бюджет ограничен, чтобы патоген нельзя было прокачать целиком.</summary>
    public void EarnDna(double amount)
    {
        if (amount <= 0.0)
        {
            return;
        }

        EarnedDna = Math.Min(DnaCap, EarnedDna + amount);
    }

    // --- Производные эпидемиологические величины ---

    public double InfectivityMultiplier => Math.Pow(InfectivityPerLevel, Level(MutationId.Infectivity));

    public double SymptomaticSpreadMultiplier => Math.Pow(SymptomaticSpreadPerLevel, Level(MutationId.SymptomaticSpread));

    /// <summary>Доля инфекционных, у которых есть симптомы. Остальные не изолируются и не попадают в статистику.</summary>
    public double SymptomaticFraction =>
        Math.Clamp(1.0 - SymptomLossPerLevel * Level(MutationId.SymptomaticSpread), 0.1, 1.0);

    public double IncubationDays =>
        Config.IncubationDays * Math.Pow(IncubationPerLevel, Level(MutationId.Incubation));

    public double InfectiousDays =>
        Config.InfectiousDays * Math.Pow(DurabilityPerLevel, Level(MutationId.Durability));

    /// <summary>Скорость перехода из инкубационного периода в инфекционный, 1/день.</summary>
    public double Sigma => 1.0 / Math.Max(0.25, IncubationDays);

    /// <summary>Скорость выздоровления без учёта медицины, 1/день.</summary>
    public double Gamma => 1.0 / Math.Max(0.25, InfectiousDays);

    /// <summary>Смертность в регионе с нулевой медициной, доля за инфекционный период.</summary>
    public double SeverityFactor =>
        0.015 * Math.Pow(SeverityPerLevel, Level(MutationId.Severity));

    /// <summary>Сопротивление лекарству и вакцине: 0..0.48.</summary>
    public double MedicineResistanceFactor =>
        Math.Clamp(MedicineResistancePerLevel * Level(MutationId.MedicineResistance), 0.0, 0.48);

    /// <summary>
    /// Эффективное репродуктивное число патогена: R0 = beta * D_inf.
    /// Базовое R0 умножается на заразность и на отсутствие симптомов.
    /// </summary>
    public double BasicReproduction =>
        Config.BaseReproductionNumber * InfectivityMultiplier * SymptomaticSpreadMultiplier;

    public bool IsVectorActive(TransmissionVector vector) => Level(VectorToMutation(vector)) > 0;

    /// <summary>Битовая маска активных векторов — ключ кэша графа контактов.</summary>
    public int VectorMask()
    {
        int mask = 0;
        foreach (TransmissionVector vector in Enum.GetValues<TransmissionVector>())
        {
            if (IsVectorActive(vector))
            {
                mask |= VectorData.ToMask(vector);
            }
        }

        return mask;
    }

    /// <summary>Множитель восприимчивости региона к патогену из-за климата, 0.2..1.</summary>
    public double ClimateSusceptibility(Climate climate)
    {
        double resistance = 0.0;
        foreach ((Climate c, MutationId trait) in ClimateTraitTable)
        {
            if (c != climate)
            {
                continue;
            }

            resistance = Math.Max(resistance, ResistancePerLevel * Level(trait));
        }

        return Math.Clamp(1.0 - resistance, 0.05, 1.0);
    }

    public static MutationId VectorToMutation(TransmissionVector vector) => vector switch
    {
        TransmissionVector.Air => MutationId.Air,
        TransmissionVector.Water => MutationId.Water,
        TransmissionVector.Surface => MutationId.Surface,
        TransmissionVector.Insects => MutationId.Insects,
        TransmissionVector.Animals => MutationId.Animals,
        _ => throw new ArgumentOutOfRangeException(nameof(vector)),
    };

    /// <summary>Начальные уровни патогена: только воздушный путь, базовые свойства.</summary>
    public static string DefaultState => "Воздушно-капельный";
}