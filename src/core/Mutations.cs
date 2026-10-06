using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>Идентификаторы всех мутаций патогена.</summary>
public enum MutationId
{
    // Пути передачи
    Air = 0,
    Water = 1,
    Surface = 2,
    Insects = 3,
    Animals = 4,

    // Свойства патогена
    Infectivity = 10,
    SymptomaticSpread = 11,
    Incubation = 12,
    Durability = 13,

    // Приспособление к популяции
    AntigenicDrift = 14,

    // Сопротивление среде
    ResistCold = 20,
    ResistHeat = 21,
    ResistHumidity = 22,
    ResistDry = 23,

    // Клиника
    Severity = 30,
    MedicineResistance = 31,
}

/// <summary>Требование к состоянию другой мутации для открытия текущей.</summary>
public readonly record struct MutationRequirement(MutationId Id, int Level);

/// <summary>Описание одной мутации: что она делает и сколько стоит.</summary>
/// <remarks>
/// Уровень 0 — исходное состояние патогена, он ничего не стоит и ничего не меняет.
/// Уровень 1 и выше покупаются за очки ДНК. Производные величины патогена
/// считаются от уровня напрямую, поэтому нулевой уровень даёт ровно базовые
/// значения из <see cref="SimulationConfig"/>.
/// </remarks>
public sealed record MutationDef(
    MutationId Id,
    string Name,
    string Effect,
    string Category,
    int MaxLevel,
    int BaseCost,
    int CostStep,
    bool StartsUnlocked,
    TransmissionVector? Vector = null,
    MutationRequirement[]? Requires = null)
{
    /// <summary>Цена покупки указанного уровня.</summary>
    public int CostOfLevel(int level)
    {
        if (level < 1 || level > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return BaseCost + (level - 1) * CostStep;
    }

    /// <summary>Цена следующего уровня относительно текущего состояния патогена.</summary>
    public int CostFromLevel(int currentLevel)
    {
        int next = currentLevel + 1;
        return next > MaxLevel ? 0 : CostOfLevel(next);
    }

    /// <summary>Суммарная цена, которую нужно заплатить, чтобы открыть всё до максимума.</summary>
    public int TotalCostToMax()
    {
        int sum = 0;
        int from = StartsUnlocked ? 2 : 1;
        for (int level = from; level <= MaxLevel; level++)
        {
            sum += CostOfLevel(level);
        }

        return sum;
    }
}

/// <summary>Каталог всех мутаций и их стоимости.</summary>
public static class MutationCatalog
{
    public const string CategoryTransmission = "Пути передачи";
    public const string CategoryProperties = "Свойства патогена";
    public const string CategoryEnvironment = "Сопротивление среде";
    public const string CategoryClinical = "Клинические признаки";

    private static readonly MutationDef[] DefinitionsArray =
    {
        // --- Пути передачи ---
        new(
            MutationId.Air,
            "Воздушно-капельный",
            "Доступен изначально. Работает везде, но плохо переносит жару и сухость.",
            CategoryTransmission,
            MaxLevel: 1, BaseCost: 0, CostStep: 0, StartsUnlocked: true,
            Vector: TransmissionVector.Air),

        new(
            MutationId.Water,
            "Водный путь",
            "Передача через открытую воду. Взрывает заразность во влажных тропиках.",
            CategoryTransmission,
            MaxLevel: 1, BaseCost: 6, CostStep: 0, StartsUnlocked: false,
            Vector: TransmissionVector.Water),

        new(
            MutationId.Surface,
            "Контактный путь",
            "Передача через бытовой контакт и поверхности. Особенно силён в городах.",
            CategoryTransmission,
            MaxLevel: 1, BaseCost: 10, CostStep: 0, StartsUnlocked: false,
            Vector: TransmissionVector.Surface),

        new(
            MutationId.Insects,
            "Насекомые-переносчики",
            "Передача через укус комара. Бьёт по жарким влажным регионам почти без сопротивления.",
            CategoryTransmission,
            MaxLevel: 1, BaseCost: 14, CostStep: 0, StartsUnlocked: false,
            Vector: TransmissionVector.Insects,
            Requires: new[] { new MutationRequirement(MutationId.Water, 1) }),

        new(
            MutationId.Animals,
            "Зоонозный путь",
            "Передача от животных к человеку. Работает в сельских регионах со скотиной.",
            CategoryTransmission,
            MaxLevel: 1, BaseCost: 20, CostStep: 0, StartsUnlocked: false,
            Vector: TransmissionVector.Animals,
            Requires: new[] { new MutationRequirement(MutationId.Surface, 1) }),

        // --- Свойства патогена ---
        new(
            MutationId.Infectivity,
            "Заразность",
            "Множитель силы передачи. Основной способ разогнать R0.",
            CategoryProperties,
            MaxLevel: 8, BaseCost: 2, CostStep: 2, StartsUnlocked: false),

        new(
            MutationId.SymptomaticSpread,
            "Передача без симптомов",
            "Доля заражённых, у которых нет симптомов, а значит — нет изоляции и обезличивания.",
            CategoryProperties,
            MaxLevel: 5, BaseCost: 3, CostStep: 3, StartsUnlocked: false),

        new(
            MutationId.Incubation,
            "Длинный инкубационный период",
            "Растягивает скрытое течение. Учёные реже замечают вспышку вовремя.",
            CategoryProperties,
            MaxLevel: 8, BaseCost: 2, CostStep: 2, StartsUnlocked: false),

        new(
            MutationId.Durability,
            "Длительный заразный период",
            "Инфекционный инфекционный период растёт — каждый больной заражает больше людей.",
            CategoryProperties,
            MaxLevel: 8, BaseCost: 2, CostStep: 2, StartsUnlocked: false),

        new(
            MutationId.AntigenicDrift,
            "Антигенный дрейф",
            "Сокращает срок иммунитета, и переболевшие заражаются повторно. " +
            "Больше заболевших и больше смертей, но выше заразность, а значит " +
            "люди быстрее замечают патоген.",
            CategoryProperties,
            MaxLevel: 5, BaseCost: 4, CostStep: 4, StartsUnlocked: false),

        // --- Сопротивление среде ---
        new(
            MutationId.ResistCold,
            "Холодоустойчивость",
            "Снижает потери в полярных и холодных регионах.",
            CategoryEnvironment,
            MaxLevel: 5, BaseCost: 4, CostStep: 3, StartsUnlocked: false),

        new(
            MutationId.ResistHeat,
            "Теплоустойчивость",
            "Снижает потери в жарких регионах.",
            CategoryEnvironment,
            MaxLevel: 5, BaseCost: 4, CostStep: 3, StartsUnlocked: false),

        new(
            MutationId.ResistHumidity,
            "Влагоустойчивость",
            "Снижает потери во влажных регионах.",
            CategoryEnvironment,
            MaxLevel: 5, BaseCost: 4, CostStep: 3, StartsUnlocked: false),

        new(
            MutationId.ResistDry,
            "Сухоустойчивость",
            "Снижает потери в засушливых регионах.",
            CategoryEnvironment,
            MaxLevel: 5, BaseCost: 4, CostStep: 3, StartsUnlocked: false),

        // --- Клиника ---
        new(
            MutationId.Severity,
            "Тяжесть болезни",
            "Растёт смертность, но взамен ускоряет исследования и приносит больше ДНК.",
            CategoryClinical,
            MaxLevel: 8, BaseCost: 2, CostStep: 2, StartsUnlocked: false),

        new(
            MutationId.MedicineResistance,
            "Устойчивость к медицине",
            "Ослабляет действие лекарства и вакцины.",
            CategoryClinical,
            MaxLevel: 5, BaseCost: 6, CostStep: 4, StartsUnlocked: false,
            Requires: new[] { new MutationRequirement(MutationId.Severity, 4) }),
    };

    private static readonly Dictionary<MutationId, MutationDef> DefinitionsById = BuildIndex();

    /// <summary>Каталог в фиксированном порядке — порядок не должен зависеть от хешей.</summary>
    public static IReadOnlyList<MutationDef> All => DefinitionsArray;

    /// <summary>Полный бюджет ДНК на все мутации. Играть можно только частью.</summary>
    public static int TotalDna { get; } = ComputeTotalDna();

    public static MutationDef Get(MutationId id) => DefinitionsById[id];

    public static MutationDef? TryGet(MutationId id) =>
        DefinitionsById.TryGetValue(id, out MutationDef? def) ? def : null;

    /// <summary>Требования к мутации как пары «мутация — нужный уровень».</summary>
    public static MutationRequirement[] RequirementsOf(MutationId id) =>
        Get(id).Requires ?? Array.Empty<MutationRequirement>();

    private static Dictionary<MutationId, MutationDef> BuildIndex()
    {
        var index = new Dictionary<MutationId, MutationDef>(DefinitionsArray.Length);
        foreach (MutationDef def in DefinitionsArray)
        {
            index[def.Id] = def;
        }

        return index;
    }

    private static int ComputeTotalDna()
    {
        int total = 0;
        foreach (MutationDef def in DefinitionsArray)
        {
            total += def.TotalCostToMax();
        }

        return total;
    }
}