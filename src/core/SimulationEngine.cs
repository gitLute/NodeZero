using System;
using System.Collections.Generic;

namespace NodeZero.Core;

/// <summary>Итог партии.</summary>
public enum GameStatus
{
    /// <summary>Партия идёт.</summary>
    Running = 0,

    /// <summary>Патоген уничтожил население мира.</summary>
    Victory = 1,

    /// <summary>В мире не осталось ни одного носителя: естественное вымирание или победа медицины.</summary>
    DefeatExtinct = 2,
}

public enum GameEventKind
{
    Outbreak = 0,
    Research = 1,
    Cure = 2,
    Vaccine = 3,
    Identification = 4,
    Mutation = 5,
    Victory = 6,
    Defeat = 7,
}

/// <summary>Запись в новостную ленту.</summary>
public readonly record struct GameEvent(int Day, GameEventKind Kind, string Text);

/// <summary>
/// Движок эпидемии: интегрирует модель SEIR по всем регионам мира.
///
/// Распространение считается потоками между регионами, а не случайными
/// событиями. За один шаг регион получает инфекционный поток из трёх источников:
/// изнутри себя, от соседей по каждому активному вектору передачи и по индексу
/// плотности контактов. Все коэффициенты — функции от состояния мира, поэтому
/// повторный прогон с тем же сидом даёт тот же результат до последнего разряда.
/// </summary>
public sealed class SimulationEngine
{
    private const int NeighborsPerCell = 6;
    private const int MaxEvents = 400;

    // Соответствие «локальный индекс суши» <-> «глобальный индекс ячейки».
    private readonly int[] _landToCell;
    private readonly int[] _cellToLand;

    // Граф контактов: вклад каждого соседа в поток данного региона и суммарный
    // вес контактов региона, нормированный на среднемировой.
    private int _cachedMask = int.MinValue;
    private double[] _intraWeight = Array.Empty<double>();
    private double[] _totalWeight = Array.Empty<double>();
    private int[] _incomingCount = Array.Empty<int>();
    private int[] _incomingLand = Array.Empty<int>();
    private double[] _incomingShare = Array.Empty<double>();
    private double[] _exportShare = Array.Empty<double>();
    private int[] _neighborCount = Array.Empty<int>();
    private int[] _neighborLand = Array.Empty<int>();
    private double _referenceWeight = 1.0;

    // Черновые массивы на шаг интегрирования.
    private readonly double[] _infectiousFraction;
    private readonly double[] _force = Array.Empty<double>();
    private readonly double[] _beta = Array.Empty<double>();
    private double[] _scratchWeights = Array.Empty<double>();
    private int[] _scratchLand = Array.Empty<int>();

    // Накопители, обновляемые раз в смоделированные сутки.
    private double _dayAccumulator;
    private double _exposedThisDay;
    private double _exitedThisDay;
    private double _exposedPerDay;
    private double _exitedPerDay;
    private int _extinctDays;
    private int _outbreakReports;
    private int _patientZeroLand = -1;
    private ResearchStage _lastStage = ResearchStage.None;
    private bool _identificationLogged;
    private bool _cureLogged;
    private bool _vaccineLogged;

    public SimulationEngine(WorldMap map, Pathogen pathogen, SimulationConfig config)
    {
        Map = map;
        Pathogen = pathogen;
        Config = config;

        int landCount = map.LandIndices.Length;
        _landToCell = new int[landCount];
        _cellToLand = new int[map.Cells.Length];
        Array.Fill(_cellToLand, -1);
        for (int i = 0; i < landCount; i++)
        {
            _landToCell[i] = map.LandIndices[i];
            _cellToLand[map.LandIndices[i]] = i;
        }

        _force = new double[landCount];
        _beta = new double[landCount];
        _infectiousFraction = new double[landCount];

        _referenceWeight = ComputeReferenceContactWeight();

        Research = new ResearchSystem(
            config,
            map.TotalPopulation * config.IdentificationThresholdFraction);

        Events = new List<GameEvent>();
        RecomputeTotals();
    }

    public WorldMap Map { get; }

    public Pathogen Pathogen { get; }

    public SimulationConfig Config { get; }

    public ResearchSystem Research { get; }

    public List<GameEvent> Events { get; }

    public double Day { get; private set; }

    public GameStatus Status { get; private set; } = GameStatus.Running;

    public int? PatientZeroCell { get; private set; }

    // --- Глобальные итоги ---

    public double TotalSusceptible { get; private set; }

    public double TotalExposed { get; private set; }

    public double TotalInfectious { get; private set; }

    public double TotalRecovered { get; private set; }

    public double TotalDead { get; private set; }

    /// <summary>
    /// Число людей, переболевших патогеном хотя бы один раз за партию.
    ///
    /// <para>Величина строго монотонна, и это её главное свойство: на неё
    /// опираются порог победы, порог идентификации и начисление очков эволюции.
    /// Счётчик заражений для этого не годится -- при угасании иммунитета один
    /// человек учитывается многократно. Сумма текущих компартментов тоже не
    /// годится -- потерявший иммунитет возвращается в восприимчивые и из суммы
    /// выпадает, хотя болел. Соответственно, охват -- это именно доля людей,
    /// а не доля населения на данный момент.</para>
    /// </summary>
    public double TotalInfectedEver { get; private set; }

    /// <summary>Доля населения, переболевшего хотя бы один раз, 0..1.</summary>
    public double InfectedEverShare => Map.TotalPopulation > 0.0
        ? TotalInfectedEver / Map.TotalPopulation
        : 0.0;

    public double TotalReported { get; private set; }

    /// <summary>Восприимчивое население с учётом вакцины.</summary>
    public double TotalEffectiveSusceptible { get; private set; }

    /// <summary>
    /// Мгновенное репродуктивное число R_t: сколько новых заражений приходится
    /// на одного инфекционного за единицу времени его пребывания в активной фазе.
    ///
    /// <para>Считается как отношение измеренных потоков: новых заражений в сутки к
    /// выбыванию из инфекционной фазы в сутки. В состоянии отсутствия болезни это
    /// даёт ровно R0 = beta * D_inf, то есть смысл величины совпадает с учебным,
    /// а численно она не зависит от того, как распределена эпидемия по карте.</para>
    /// </summary>
    public double InstantaneousReproduction
    {
        get
        {
            if (_exitedPerDay <= 1e-9 || TotalInfectious <= 1e-9)
            {
                return 0.0;
            }

            return Math.Clamp(_exposedPerDay / _exitedPerDay, 0.0, 99.9);
        }
    }

    /// <summary>Восприимчивое население регионов, где идёт эпидемия.</summary>
    public double AffectedSusceptible { get; private set; }

    public int LandCellCount => _landToCell.Length;

    /// <summary>
    /// Относительный инфекционный вес региона: отношение суммарного веса активных
    /// путей передачи к среднемировой нормировке. Значение больше единицы означает,
    /// что патоген здесь распространяется легче, чем в среднем по миру.
    /// </summary>
    public double RelativeContactWeight(int cellIndex)
    {
        int local = _cellToLand[cellIndex];
        if (local < 0)
        {
            return 0.0;
        }

        EnsureContactGraph(Pathogen.VectorMask());
        return _totalWeight[local] / _referenceWeight;
    }

    public HexCell CellOfLocalIndex(int localIndex) => Map.Cells[_landToCell[localIndex]];

    public int LocalIndexOfCell(int cellIndex) => _cellToLand[cellIndex];

    /// <summary>
    /// Расчётная дата в игровых годах: один год — 360 суток.
    /// </summary>
    public double Years => Day / 360.0;

    // --- Управление партией ---

    /// <summary>
    /// Запуск эпидемии в выбранном регионе. Размер очага задан долей населения
    /// с нижней границей в несколько человек, чтобы вспышка не погибла из-за
    /// округления на дробных числах.
    /// </summary>
    public void StartOutbreak(int cellIndex, double? fraction = null)
    {
        if (Status != GameStatus.Running || cellIndex < 0 || cellIndex >= Map.Cells.Length)
        {
            return;
        }

        HexCell cell = Map.Cells[cellIndex];
        if (!cell.IsLand || cell.Population <= 0.0)
        {
            return;
        }

        double share = fraction ?? Config.PatientZeroFraction;
        double initial = Math.Max(Config.PatientZeroMinimum, cell.Population * share);

        cell.Exposed = Math.Min(initial, cell.Susceptible);
        cell.Infectious = 0.0;

        PatientZeroCell = cellIndex;
        _patientZeroLand = _cellToLand[cellIndex];
        cell.OutbreakReported = true;

        AddEvent(GameEventKind.Outbreak,
            $"Нулевой пациент: регион {cell.Coord} ({ClimateData.Name(cell.Climate)}), " +
            $"население {WorldGenerator.FormatPopulation(cell.Population)}.");
        RecomputeTotals();
    }

    /// <summary>
    /// Выбор кандидата на нулевого пациента: крупные регионы взвешены по
    /// населению. Результат зависит только от сида.
    /// </summary>
    public int SuggestPatientZero()
    {
        var rng = new DeterministicRandom(Config.Seed ^ 0x9E3779B97F4A7C15UL);
        double totalWeight = 0.0;
        foreach (int index in Map.LandIndices)
        {
            totalWeight += Map.Cells[index].Population;
        }

        if (totalWeight <= 0.0)
        {
            return Map.LandIndices.Length > 0 ? Map.LandIndices[0] : -1;
        }

        double pick = rng.Range(0.0, totalWeight);
        double accumulated = 0.0;
        foreach (int index in Map.LandIndices)
        {
            accumulated += Map.Cells[index].Population;
            if (accumulated >= pick)
            {
                return index;
            }
        }

        return Map.LandIndices[^1];
    }

    /// <summary>Покупка мутации с записью в ленту событий.</summary>
    public bool TryMutate(MutationId id, out string? refusal)
    {
        MutationDef def = MutationCatalog.Get(id);
        int before = Pathogen.Level(id);
        if (!Pathogen.TryMutate(id, out refusal))
        {
            return false;
        }

        int cost = def.CostOfLevel(before + 1);
        AddEvent(GameEventKind.Mutation,
            $"Мутация «{def.Name}» до уровня {before + 1}, потрачено {cost} ДНК.");
        return true;
    }

    // --- Интегрирование ---

    /// <summary>
    /// Продвинуть симуляцию на указанное число суток. Дробный шаг Эйлера
    /// подбирается автоматически, поэтому можно просить и 0.02 суток за кадр,
    /// и несколько лет за один вызов.
    /// </summary>
    public void AdvanceDays(double days)
    {
        if (Status != GameStatus.Running || days <= 0.0 || double.IsNaN(days))
        {
            return;
        }

        EnsureContactGraph(Pathogen.VectorMask());

        int steps = (int)Math.Ceiling(days / Config.SubStepDays);
        if (steps < 1)
        {
            steps = 1;
        }

        double dt = days / steps;
        for (int step = 0; step < steps && Status == GameStatus.Running; step++)
        {
            SubStep(dt);
        }
    }

    private void SubStep(double dt)
    {
        Day += dt;

        // Доли инфекционных считаются по состоянию начала шага: иначе при обходе
        // соседей часть регионов считалась бы по новому состоянию, а часть — по старому,
        // и результат зависел бы от порядка обхода.
        for (int i = 0; i < _landToCell.Length; i++)
        {
            _infectiousFraction[i] = CellOfLocalIndex(i).InfectiousFraction;
        }

        double exposedThisStep = 0.0;
        double exitedThisStep = 0.0;
        double lostImmunityStep = 0.0;
        for (int i = 0; i < _landToCell.Length; i++)
        {
            HexCell cell = CellOfLocalIndex(i);
            _force[i] = ComputeForce(i);
            _beta[i] = ComputeBeta(i, cell);
            IntegrateCell(i, cell, dt, ref exposedThisStep, ref exitedThisStep, ref lostImmunityStep);
        }

        _lostImmunityTotal += lostImmunityStep;

        _exposedThisDay += exposedThisStep;
        _exitedThisDay += exitedThisStep;
        _dayAccumulator += dt;

        RecomputeTotals();

        while (_dayAccumulator >= 1.0)
        {
            _dayAccumulator -= 1.0;
            _exposedPerDay = _exposedThisDay;
            _exitedPerDay = _exitedThisDay;
            _exposedThisDay = 0.0;
            _exitedThisDay = 0.0;
            ProcessWholeDay();
        }
    }

    /// <summary>
    /// Инфекционный поток на душу населения региона.
    ///
    /// <para>Складываются две составляющие: собственная инфекционность региона и
    /// вклад соседей. Первая идёт в множитель, задаваемый R0, вторая — отдельным
    /// потоком с собственным коэффициентом, см. <see cref="VectorData.CrossBorderRate"/>.</para>
    /// </summary>
    private double ComputeForce(int localIndex)
    {
        double force = _infectiousFraction[localIndex];

        int slot = localIndex * NeighborsPerCell;
        int count = _incomingCount[localIndex];
        for (int k = 0; k < count; k++)
        {
            force += _incomingShare[slot + k] * _infectiousFraction[_incomingLand[slot + k]];
        }

        return force;
    }

    /// <summary>
    /// Коэффициент передачи beta для региона. Собственное R0 делится на
    /// инфекционный период и поправляется на плотность контактов, климат,
    /// действие лекарства и то, как быстро больных выявляют и изолируют.
    /// </summary>
    private double ComputeBeta(int localIndex, HexCell cell)
    {
        double contactFactor = _totalWeight[localIndex] / _referenceWeight;
        double climateFactor = Pathogen.ClimateSusceptibility(cell.Climate);
        double cureFactor = 1.0 - 0.92 * Research.CureEffectiveness;

        // Выявляемость пропорциональна уровню медицины, изоляция — его же уровню,
        // а доля симптомных задана мутациями патогена.
        double detection = 0.25 + 0.60 * cell.Healthcare;
        double isolation = 0.70 * (0.40 + 0.60 * cell.Healthcare);
        double symptomFactor = Math.Clamp(
            1.0 - detection * Pathogen.SymptomaticFraction * isolation,
            0.05,
            1.0);

        return Pathogen.BasicReproduction / Pathogen.InfectiousDays
               * contactFactor * climateFactor * cureFactor * symptomFactor;
    }

    /// <summary>Смертность региона с учётом медицины, лекарства и устойчивости патогена.</summary>
    private double DeathRate(HexCell cell)
    {
        double medicare = 1.0 - 0.75 * cell.Healthcare;
        double cure = 1.0 - 0.60 * Research.CureEffectiveness;
        double resistance = 1.0 + Pathogen.MedicineResistanceFactor;
        return Pathogen.SeverityFactor * medicare * cure / resistance;
    }

    /// <summary>Шаг SEIR для одного региона. Возвращает число новых инфицированных.</summary>
    private double _lostImmunityTotal;

    /// <summary>
    /// Число человек, потерявших иммунитет за всю партию и снова ставших
    /// восприимчивыми. Нужен для проверки угасания иммунитета: само число
    /// переболевших не показывает, вернулись ли они в строй.
    /// </summary>
    public double TotalLostImmunity => _lostImmunityTotal;

    /// <summary>Шаг SEIR для одного региона. Через ref-параметры возвращает потоки.</summary>
    /// <param name="localIndex">Индекс региона в массиве наземных.</param>
    /// <param name="cell">Сам регион.</param>
    /// <param name="dt">Шаг интегрирования, сутки.</param>
    /// <param name="exposedOut">Новые заражённые за шаг.</param>
    /// <param name="exitedOut">Выбывшие из активной фазы за шаг.</param>
    /// <param name="refLostImmunity">Потерявшие иммунитет за шаг.</param>
    private void IntegrateCell(
        int localIndex,
        HexCell cell,
        double dt,
        ref double exposedOut,
        ref double exitedOut,
        ref double refLostImmunity)
    {
        if (cell.LivingPopulation <= 0.0)
        {
            return;
        }

        // Угасание иммунитета. Применяется к запасу переболевших на начало шага,
        // чтобы только что выздоровевшие не теряли иммунитет в том же шаге.
        // Доля теряющих иммунитет считается точно для постоянной скорости
        // (1 - exp(-dt/срок)), а не приближённо как dt/срок: приближение при
        // крупном шаге способно дать отрицательный остаток.
        double waning = Pathogen.WaningFraction(dt);
        if (waning > 0.0 && cell.Recovered > 0.0)
        {
            double reSuscepted = cell.Recovered * waning;
            cell.Recovered -= reSuscepted;
            cell.Susceptible += reSuscepted;
            refLostImmunity += reSuscepted;
        }

        double vaccine = Config.VaccineEffectiveness * cell.VaccineCoverage
                         * (1.0 - 0.6 * Pathogen.MedicineResistanceFactor);
        double effectiveSusceptible = cell.Susceptible * Math.Clamp(1.0 - vaccine, 0.0, 1.0);

        double newExposed = effectiveSusceptible * _beta[localIndex] * _force[localIndex] * dt;

        // Ограничиваем долю заражённых за один шаг: это защищает явную схему
        // Эйлера от выхода за физический смысл при резком росте.
        double ceiling = effectiveSusceptible * 0.4;
        if (newExposed > ceiling)
        {
            newExposed = ceiling;
        }
        else if (newExposed < 0.0)
        {
            newExposed = 0.0;
        }

        // Из новых заражённых первый раз болеют только те, кто ещё не болел.
        // Ограничение снизу не даёт счётчику уйти в отрицательные значения,
        // когда бо́льшая часть потока приходится на повторные болезни.
        double firstTime = Math.Min(newExposed, cell.NeverInfectedSusceptible);
        cell.NeverInfectedSusceptible -= firstTime;

        double toInfectious = cell.Exposed * Math.Min(1.0, Pathogen.Sigma * dt);

        double gamma = Math.Min(1.0, Pathogen.Gamma * (1.0 + 0.35 * cell.Healthcare) * dt);
        double toRecovered = cell.Infectious * gamma;
        double toDead = cell.Infectious * Math.Min(1.0, DeathRate(cell) * dt);

        double leaving = toRecovered + toDead;
        if (leaving > cell.Infectious)
        {
            double scale = leaving > 0.0 ? cell.Infectious / leaving : 0.0;
            toRecovered *= scale;
            toDead *= scale;
            leaving = cell.Infectious;
        }

        cell.Susceptible = NonNegative(cell.Susceptible - newExposed);
        cell.Exposed = NonNegative(cell.Exposed + newExposed - toInfectious);
        cell.Infectious = NonNegative(cell.Infectious + toInfectious - leaving);
        cell.Recovered += toRecovered;
        cell.Dead += toDead;

        // Статистика, которую видят учёные: не реальные случаи, а выявленные.
        cell.ReportedCases += toInfectious * (0.25 + 0.60 * cell.Healthcare) * 0.7;

        if (Research.Stage == ResearchStage.Vaccine)
        {
            double rate = (0.0020 + 0.0120 * cell.Healthcare)
                          * (1.0 - 0.5 * Pathogen.MedicineResistanceFactor);
            cell.VaccineCoverage += (1.0 - cell.VaccineCoverage) * rate * dt;
        }

        exposedOut += newExposed;
        exitedOut += leaving;
    }

    private static double NonNegative(double value) => value > 0.0 ? value : 0.0;

    /// <summary>Обработка целых суток: исследования, очки ДНК, события, проверка исхода.</summary>
    private void ProcessWholeDay()
    {
        double noticeable = TotalInfectious + 0.35 * TotalDead;

        Research.Advance(
            1.0,
            noticeable,
            Pathogen.SeverityFactor,
            TotalInfectedEver,
            Pathogen.MedicineResistanceFactor);

        double dna = (Config.DnaBaseRate + Config.DnaInfectionRate * Math.Sqrt(Math.Max(0.0, InfectedEverShare)))
                     * (1.0 + 0.5 * Pathogen.SeverityFactor);
        Pathogen.EarnDna(dna);

        ReportStageChanges();
        ReportOutbreaks();
        UpdateStatus();
    }

    private void ReportStageChanges()
    {
        if (Research.ResearchResumedThisStep)
        {
            AddEvent(GameEventKind.Research,
                "Исследования возобновлены: слишком долгое замалчивание вызвало вопросы.");
        }

        if (Research.Identified && !_identificationLogged)
        {
            _identificationLogged = true;
            AddEvent(GameEventKind.Identification,
                "Патоген идентифицирован. Исследования ускоряются: о нём узнали, и деньги на них нашлись.");
        }

        if (Research.Stage != _lastStage)
        {
            if (Research.Stage == ResearchStage.Cure && !_cureLogged)
            {
                _cureLogged = true;
                AddEvent(GameEventKind.Cure,
                    "Первое противопатогенное средство разработано. Его эффективность будет нарастать.");
            }
            else if (Research.Stage == ResearchStage.Vaccine && !_vaccineLogged)
            {
                _vaccineLogged = true;
                AddEvent(GameEventKind.Vaccine,
                    "Вакцина поступила в массовое производство. Начинается вакцинация населения.");
            }

            _lastStage = Research.Stage;
        }
    }

    /// <summary>
    /// Новостная лента. За один шаг добавляется не больше одной вспышки,
    /// иначе при массовой эпидемии лента переполнялась бы сотнями строк.
    /// </summary>
    private void ReportOutbreaks()
    {
        if (_outbreakReports >= Config.MaxOutbreakReports)
        {
            return;
        }

        for (int i = 0; i < _landToCell.Length; i++)
        {
            if (i == _patientZeroLand)
            {
                continue;
            }

            HexCell cell = CellOfLocalIndex(i);
            if (cell.OutbreakReported)
            {
                continue;
            }

            if (cell.ReportedCases < 500.0 && cell.Infectious < 1000.0)
            {
                continue;
            }

            cell.OutbreakReported = true;
            _outbreakReports++;
            AddEvent(GameEventKind.Outbreak,
                $"Вспышка в регионе {cell.Coord}, климат {ClimateData.Name(cell.Climate)}: " +
                $"{WorldGenerator.FormatPopulation(cell.Infectious)} инфекционных, " +
                $"медицина {cell.Healthcare * 100.0:0}%.");
            return;
        }
    }

    private void UpdateStatus()
    {
        if (Status != GameStatus.Running)
        {
            return;
        }

        if (Map.TotalPopulation > 0.0
            && InfectedEverShare >= Config.VictoryInfectionThreshold)
        {
            Status = GameStatus.Victory;
            AddEvent(GameEventKind.Victory,
                $"Победа: переболело {InfectedEverShare * 100.0:0.##}% населения, " +
                $"уничтожено {WorldGenerator.FormatPopulation(TotalDead)} за {Years:0.0} лет.");
            return;
        }

        // Патоген считается уничтоженным, если несколько суток подряд в мире
        // нет ни одного инфекционного и ни одного инкубирующего.
        if (PatientZeroCell is not null && TotalExposed + TotalInfectious < 1.0)
        {
            _extinctDays++;
            if (_extinctDays >= Config.ExtinctionGraceDays)
            {
                Status = GameStatus.DefeatExtinct;
                AddEvent(GameEventKind.Defeat, "Поражение: все носители патогена погибли или выздоровели.");
            }
        }
        else
        {
            _extinctDays = 0;
        }
    }

    private void AddEvent(GameEventKind kind, string text)
    {
        Events.Add(new GameEvent((int)Math.Floor(Day), kind, text));
        if (Events.Count > MaxEvents)
        {
            Events.RemoveAt(0);
        }
    }

    private void RecomputeTotals()
    {
        double susceptible = 0.0;
        double exposed = 0.0;
        double infectious = 0.0;
        double recovered = 0.0;
        double dead = 0.0;
        double reported = 0.0;
        double everInfected = 0.0;
        double effectiveSusceptible = 0.0;
        double affectedSusceptible = 0.0;

        double medicineResistanceFactor = Pathogen.MedicineResistanceFactor;

        foreach (int cellIndex in _landToCell)
        {
            HexCell cell = Map.Cells[cellIndex];
            susceptible += cell.Susceptible;
            exposed += cell.Exposed;
            infectious += cell.Infectious;
            recovered += cell.Recovered;
            dead += cell.Dead;
            reported += cell.ReportedCases;
            everInfected += cell.EverInfected;

            double vaccine = Config.VaccineEffectiveness * cell.VaccineCoverage
                             * (1.0 - 0.6 * medicineResistanceFactor);
            double effective = cell.Susceptible * Math.Clamp(1.0 - vaccine, 0.0, 1.0);
            effectiveSusceptible += effective;

            if (cell.Exposed + cell.Infectious > 0.0)
            {
                affectedSusceptible += effective;
            }
        }

        TotalSusceptible = susceptible;
        TotalExposed = exposed;
        TotalInfectious = infectious;
        TotalRecovered = recovered;
        TotalDead = dead;
        TotalReported = reported;
        TotalInfectedEver = everInfected;
        TotalEffectiveSusceptible = effectiveSusceptible;
        AffectedSusceptible = affectedSusceptible;
    }

    /// <summary>
    /// Нормировка контактов: средний по миру вес контакта для исходного набора
    /// векторов (только воздушный). Фиксируется один раз и больше не меняется,
    /// поэтому открытие нового пути передачи действительно повышает заразность,
    /// а не подгоняет нормировку под себя.
    /// </summary>
    private double ComputeReferenceContactWeight()
    {
        if (_landToCell.Length == 0)
        {
            return 1.0;
        }

        double sum = 0.0;

        foreach (int cellIndex in _landToCell)
        {
            HexCell cell = Map.Cells[cellIndex];
            double weighted = VectorData.BaseWeight(TransmissionVector.Air)
                              * VectorData.Suitability(TransmissionVector.Air, cell);
            sum += weighted * VectorData.IntraRegionScale * cell.ContactDensity;
        }

        double average = sum / _landToCell.Length;
        return average > 1e-9 ? average : 1.0;
    }

    /// <summary>
    /// Перестроение графа контактов при изменении набора путей передачи.
    ///
    /// <para>Считаются две величины: внутрирегиональный вес контактов (влияет на
    /// то, насколько быстро патоген выжигает регион) и экспортные доли в соседей
    /// (влияют на скорость продвижения фронта). Экспортные доли нормированы на
    /// сумму по соседям региона и затем умножаются на коэффициент миграции.</para>
    /// </summary>
    private void EnsureContactGraph(int mask)
    {
        if (mask == _cachedMask)
        {
            return;
        }

        int landCount = _landToCell.Length;
        _intraWeight = new double[landCount];
        _totalWeight = new double[landCount];
        _incomingCount = new int[landCount];
        _incomingLand = new int[landCount * NeighborsPerCell];
        _incomingShare = new double[landCount * NeighborsPerCell];
        _exportShare = new double[landCount * NeighborsPerCell];
        _neighborCount = new int[landCount];
        _neighborLand = new int[landCount * NeighborsPerCell];
        _scratchWeights = new double[NeighborsPerCell];
        _scratchLand = new int[NeighborsPerCell];

        for (int i = 0; i < landCount; i++)
        {
            HexCell cell = CellOfLocalIndex(i);

            int neighborTotal = 0;
            foreach (int neighborIndex in cell.Neighbors)
            {
                int localNeighbor = _cellToLand[neighborIndex];
                if (localNeighbor < 0)
                {
                    continue;
                }

                _scratchLand[neighborTotal] = localNeighbor;
                _scratchWeights[neighborTotal] = 0.0;
                neighborTotal++;
            }

            double intra = 0.0;

            for (int v = 0; v < VectorData.Count; v++)
            {
                if ((mask & (1 << v)) == 0)
                {
                    continue;
                }

                var vector = (TransmissionVector)v;
                double suitabilityHere = VectorData.Suitability(vector, cell);
                if (suitabilityHere <= 0.0)
                {
                    continue;
                }

                double weighted = VectorData.BaseWeight(vector) * suitabilityHere;
                intra += weighted * VectorData.IntraRegionScale * cell.ContactDensity;

                for (int k = 0; k < neighborTotal; k++)
                {
                    HexCell neighbor = CellOfLocalIndex(_scratchLand[k]);
                    _scratchWeights[k] += weighted * VectorData.Suitability(vector, neighbor);
                }
            }

            double crossSum = 0.0;
            for (int k = 0; k < neighborTotal; k++)
            {
                crossSum += _scratchWeights[k];
            }

            _intraWeight[i] = intra;
            _totalWeight[i] = intra;
            _neighborCount[i] = neighborTotal;

            int slot = i * NeighborsPerCell;
            for (int k = 0; k < neighborTotal; k++)
            {
                _neighborLand[slot + k] = _scratchLand[k];
                _exportShare[slot + k] = crossSum > 0.0
                    ? _scratchWeights[k] / crossSum
                    : 0.0;
            }
        }

        // Разворот графа: для каждого региона собирается список соседей с их
        // вкладом в поток именно этого региона.
        for (int i = 0; i < landCount; i++)
        {
            int slot = i * NeighborsPerCell;
            for (int k = 0; k < _neighborCount[i]; k++)
            {
                int neighbor = _neighborLand[slot + k];
                double share = VectorData.CrossBorderRate * _exportShare[slot + k];

                int targetSlot = neighbor * NeighborsPerCell + _incomingCount[neighbor];
                _incomingLand[targetSlot] = i;
                _incomingShare[targetSlot] = share;
                _incomingCount[neighbor]++;
            }
        }

        _cachedMask = mask;
    }

    /// <summary>
    /// Диагностика графа контактов для региона: разбивка веса на внутрирегиональную
    /// и межрегиональную части. Нужна при отладке формулы потоков.
    /// </summary>
    /// <summary>
    /// Дешёвый хеш наблюдаемого состояния. Нужен представлению, чтобы перерисовывать
    /// карту только тогда, когда состояние действительно изменилось. Учитываются
    /// агрегаты, а не все компартменты: любая заметная перемена агрегатов отражается
    /// на карте, а лишние перерисовки безвредны.
    /// </summary>
    public ulong StateHash()
    {
        ulong hash = 14695981039346656037UL;
        hash ^= (ulong)Math.Floor(Day * 16.0);
        hash *= 1099511628211UL;
        hash ^= (ulong)Math.Floor(TotalInfectious);
        hash *= 1099511628211UL;
        hash ^= (ulong)Math.Floor(TotalDead);
        hash *= 1099511628211UL;
        hash ^= (ulong)Math.Floor(TotalSusceptible);
        hash *= 1099511628211UL;
        hash ^= (ulong)Math.Floor(TotalExposed);
        hash *= 1099511628211UL;
        hash ^= (ulong)Math.Floor(TotalRecovered);
        hash *= 1099511628211UL;
        hash ^= (ulong)Research.Progress;
        hash *= 1099511628211UL;
        hash ^= (ulong)(long)Status;
        hash *= 1099511628211UL;
        return hash;
    }

    public string ContactDiagnostics(int cellIndex)
    {
        int local = _cellToLand[cellIndex];
        if (local < 0)
        {
            return "регион отсутствует на карте";
        }

        EnsureContactGraph(Pathogen.VectorMask());
        HexCell cell = Map.Cells[cellIndex];

        int incomingTotal = 0;
        foreach (int count in _incomingCount)
        {
            incomingTotal += count;
        }

        double total = _totalWeight[local];
        double incoming = 0.0;
        for (int k = 0; k < _incomingCount[local]; k++)
        {
            incoming += _incomingShare[local * NeighborsPerCell + k];
        }

        System.Text.StringBuilder neighbors = new();
        for (int k = 0; k < _neighborCount[local]; k++)
        {
            int neighbor = _neighborLand[local * NeighborsPerCell + k];
            neighbors.Append(' ')
                .Append(neighbor)
                .Append("(in=")
                .Append(_incomingCount[neighbor])
                .Append(",back=")
                .Append(_exportShare[neighbor * NeighborsPerCell] >= 0.0 ? 'y' : 'n')
                .Append(')');
        }

        System.Text.StringBuilder head = new();
        for (int i = 0; i < 6 && i < _landToCell.Length; i++)
        {
            HexCell c = CellOfLocalIndex(i);
            head.Append('{').Append(c.Coord).Append('=');
            foreach (int n in c.Neighbors)
            {
                head.Append(_cellToLand[n]).Append(',');
            }

            head.Append('}');
        }

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "intra={0:0.0000}  incomingN={1}  incomingTotal={2}  migration={3:0.0000}  exportN={4}  " +
            "total={5:0.0000}  reference={6:0.0000}  relative={7:0.0000}  land={8}  cachedMask={9}" +
            "  соседи:{10}  начало:{11}",
            _intraWeight[local],
            _incomingCount[local],
            incomingTotal,
            incoming,
            _neighborCount[local],
            total,
            _referenceWeight,
            total / _referenceWeight,
            _landToCell.Length,
            _cachedMask,
            neighbors.ToString(),
            head.ToString());
    }

    /// <summary>
    /// Эффективный коэффициент передачи beta в регионе и составляющие его множители.
    /// </summary>
    public string BetaDiagnostics(int cellIndex)
    {
        int local = _cellToLand[cellIndex];
        if (local < 0)
        {
            return "регион отсутствует на карте";
        }

        EnsureContactGraph(Pathogen.VectorMask());
        HexCell cell = Map.Cells[cellIndex];
        double contactFactor = _totalWeight[local] / _referenceWeight;
        double climateFactor = Pathogen.ClimateSusceptibility(cell.Climate);
        double cureFactor = 1.0 - 0.92 * Research.CureEffectiveness;
        double detection = 0.25 + 0.60 * cell.Healthcare;
        double isolation = 0.70 * (0.40 + 0.60 * cell.Healthcare);
        double symptomFactor = Math.Clamp(1.0 - detection * Pathogen.SymptomaticFraction * isolation, 0.05, 1.0);

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "beta={0:0.00000}/сут  contact={1:0.000}  climate={2:0.000}  cure={3:0.000}  symptom={4:0.000}  " +
            "gamma={5:0.00000}/сут  R_eff={6:0.00}  R0={7:0.00}",
            ComputeBeta(local, cell),
            contactFactor,
            climateFactor,
            cureFactor,
            symptomFactor,
            Pathogen.Gamma * (1.0 + 0.35 * cell.Healthcare),
            ComputeBeta(local, cell) / (Pathogen.Gamma * (1.0 + 0.35 * cell.Healthcare)),
            Pathogen.BasicReproduction);
    }

    /// <summary>Краткий текстовый снимок состояния для отладки и отчётов.</summary>
    public string Snapshot(int indent = 0)
    {
        string pad = new(' ', indent);
        return string.Join(
            Environment.NewLine,
            $"{pad}День {Day:0} ({Years:0.00} лет)   Статус: {Status}",
            $"{pad}R0 базовое: {Pathogen.BasicReproduction:0.000}   R_t: {InstantaneousReproduction:0.000}   sigma: {Pathogen.Sigma:0.000}   gamma: {Pathogen.Gamma:0.000}",
            $"{pad}Инфицировано: {WorldGenerator.FormatPopulation(TotalInfectious)}   " +
            $"Умерло: {WorldGenerator.FormatPopulation(TotalDead)}   " +
            $"Выявлено: {WorldGenerator.FormatPopulation(TotalReported)}",
            $"{pad}Исследования: прогресс {Research.Progress:0.0}, стадия {Research.Stage}, " +
            $"лекарство {Research.CureEffectiveness * 100.0:0}%, идентификация {(Research.Identified ? "да" : "нет")}",
            $"{pad}ДНК: заработано {Pathogen.EarnedDna:0.#}, потрачено {Pathogen.SpentDna:0.#}, доступно {Pathogen.AvailableDna:0.#}");
    }
}