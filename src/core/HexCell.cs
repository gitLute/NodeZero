namespace NodeZero.Core;

/// <summary>
/// Регион мира: один гекс карты. Статические свойства задаются генератором мира,
/// динамические (S, E, I, R, D) меняет <see cref="SimulationEngine"/>.
/// </summary>
public sealed class HexCell
{
    // --- Топология ---

    /// <summary>Индекс ячейки в массиве мира.</summary>
    public int Index { get; set; } = -1;

    public HexCoord Coord { get; set; }

    /// <summary>Соседи по индексу. У края карты соседей меньше шести.</summary>
    public int[] Neighbors { get; set; } = System.Array.Empty<int>();

    /// <summary>Суша или океан. Океан не участвует в эпидемии, но рисуется.</summary>
    public bool IsLand { get; set; }

    // --- Статические свойства региона ---

    public Climate Climate { get; set; }

    /// <summary>Исходное население региона в людях.</summary>
    public double Population { get; set; }

    /// <summary>Уровень медицины, 0..1. Ускоряет выздоровление, снижает смертность и повышает выявляемость.</summary>
    public double Healthcare { get; set; }

    /// <summary>Доля городского населения, 0..1.</summary>
    public double Urbanization { get; set; }

    /// <summary>Доступность открытой воды, 0..1.</summary>
    public double WaterAvailability { get; set; }

    /// <summary>Пригодность обитания переносчиков, 0..1.</summary>
    public double VectorHabitat { get; set; }

    /// <summary>Поголовье животных, 0..1.</summary>
    public double Livestock { get; set; }

    /// <summary>
    /// Относительная интенсивность контактов внутри региона. 1.0 — среднее по миру.
    /// Плотные регионы передают инфекцию внутри себя сильнее.
    /// </summary>
    public double ContactDensity { get; set; } = 1.0;

    /// <summary>Качество земли, 0..1. Влияет на плотность населения.</summary>
    public double LandQuality { get; set; }

    // --- Состояние SEIR ---

    /// <summary>Восприимчивые, не защищённые вакциной.</summary>
    public double Susceptible { get; set; }

    /// <summary>Заражённые в инкубационном периоде. Уже неразрушимы для патогена.</summary>
    public double Exposed { get; set; }

    /// <summary>Инфекционные.</summary>
    public double Infectious { get; set; }

    /// <summary>Переболевшие и выздоровевшие.</summary>
    public double Recovered { get; set; }

    public double Dead { get; set; }

    /// <summary>Покрытие вакциной, 0..1.</summary>
    public double VaccineCoverage { get; set; }

    /// <summary>
    /// Число жителей, которые ещё ни разу не болели. Величина только убывает.
    /// </summary>
    /// <remarks>
    /// Нужна, чтобы отличать «болели один раз» от «болели многократно». Прямой
    /// подсчёт заражений дал бы двойной счёт при повторных болезнях, а сумма
    /// текущих компартментов S+E+I+R+D без неё врёт: утративший иммунитет
    /// возвращается в S и из выражения выпадает. Разность
    /// <c>Population - NeverInfectedSusceptible</c> даёт число людей, переболевших
    /// хотя бы один раз, и остаётся монотонной при любом сроке иммунитета.
    /// </remarks>
    public double NeverInfectedSusceptible { get; set; }

    /// <summary>Накопленное число выявленных (а не фактических) случаев. Именно оно видно учёным.</summary>
    public double ReportedCases { get; set; }

    /// <summary>Признак, что вспышка в регионе уже попала в новости.</summary>
    public bool OutbreakReported { get; set; }

    /// <summary>Живое население региона: все, кроме умерших.</summary>
    public double LivingPopulation => Susceptible + Exposed + Infectious + Recovered;

    /// <summary>Доля живого населения, которая сейчас инфицирована. Используется как мера заразности региона.</summary>
    public double InfectiousFraction
    {
        get
        {
            double living = LivingPopulation;
            return living > 0.0 ? Infectious / living : 0.0;
        }
    }

    public void InitializePopulation()
    {
        Susceptible = Population;
        Exposed = 0.0;
        Infectious = 0.0;
        Recovered = 0.0;
        Dead = 0.0;
        VaccineCoverage = 0.0;
        ReportedCases = 0.0;
        OutbreakReported = false;
        NeverInfectedSusceptible = Population;
    }

    /// <summary>Сумма всех компартментально значимых величин. Должна совпадать с исходным населением.</summary>
    public double TotalCompartments => Susceptible + Exposed + Infectious + Recovered + Dead;

    /// <summary>
    /// Число жителей, переболевших хотя бы один раз. Монотонно растёт.
    /// </summary>
    public double EverInfected => Population - NeverInfectedSusceptible;

    public void ResetDynamicState()
    {
        InitializePopulation();
    }
}