namespace NodeZero.Core;

/// <summary>
/// Все параметры симуляции и генерации мира. Собраны в одном объекте, чтобы
/// прогон был полностью описан этими числами и мог быть воспроизведён.
/// </summary>
public sealed class SimulationConfig
{
    // --- Мир ---

    /// <summary>Сид мира и патогена. Один сид — одна карта и один и тот же ход эпидемии.</summary>
    public ulong Seed { get; set; } = 20261003UL;

    /// <summary>Радиус гексагонального диска карты.</summary>
    public int MapRadius { get; set; } = 12;

    /// <summary>Целевое население мира. Подбирается нормировкой, а не вручную.</summary>
    public double TargetWorldPopulation { get; set; } = 1.5e9;

    // --- Численный интегратор ---

    /// <summary>
    /// Длина шага явного метода Эйлера в днях. Шаг подобран так, чтобы за один
    /// шаг ни один компартмент не мог перейти в себя больше своей величины.
    /// </summary>
    public double SubStepDays { get; set; } = 0.25;

    // --- Патоген по умолчанию ---

    /// <summary>Базовое репродуктивное число R0 = beta * D_inf.</summary>
    public double BaseReproductionNumber { get; set; } = 2.4;

    public double IncubationDays { get; set; } = 5.0;

    public double InfectiousDays { get; set; } = 9.0;

    // --- Исследования и медицина ---

    /// <summary>Прогресс исследований, необходимый для появления лекарства.</summary>
    public double CureThreshold { get; set; } = 140.0;

    /// <summary>Прогресс исследований, необходимый для появления вакцины.</summary>
    public double VaccineThreshold { get; set; } = 330.0;

    /// <summary>За сколько дней эффективность лекарства набирается от нуля до полной.</summary>
    public double CureDeploymentDays { get; set; } = 300.0;

    /// <summary>Эффективность вакцины в идеальном случае.</summary>
    public double VaccineEffectiveness { get; set; } = 0.95;

    /// <summary>
    /// Доля населения, при превышении которой патоген идентифицируют. Само по себе
    /// это не поражение, но после идентификации исследования ускоряются: мир узнал,
    /// что с ним происходит, и финансирование исследований растёт.
    /// </summary>
    public double IdentificationThresholdFraction { get; set; } = 0.10;

    /// <summary>Во сколько раз ускоряются исследования после идентификации патогена.</summary>
    public double IdentificationResearchBonus { get; set; } = 1.8;

    /// <summary>Сколько суток исследования могут быть остановлены подряд.</summary>
    public double ResearchSuppressionLimitDays { get; set; } = 60.0;

    /// <summary>Базовый темп накопления прогресса исследований.</summary>
    public double ResearchGain { get; set; } = 0.5;

    /// <summary>Насколько ускоряет исследования тяжесть болезни (люди боятся).</summary>
    public double ResearchSeverityWeight { get; set; } = 0.9;

    // --- Экономика мутаций ---

    /// <summary>Базовый темп начисления очков ДНК в день.</summary>
    public double DnaBaseRate { get; set; } = 0.05;

    /// <summary>Темп начисления ДНК, зависящий от доли заражённых.</summary>
    public double DnaInfectionRate { get; set; } = 0.40;

    /// <summary>Доля от полного бюджета ДНК, которую вообще можно накопить.</summary>
    public double DnaEarnCap { get; set; } = 0.85;

    // --- Условия победы и поражения ---

    /// <summary>
    /// Доля населения, которая должна быть охвачена инфекцией, чтобы патоген
    /// считался победившим.
    /// </summary>
    /// <remarks>
    /// Победа измеряется заражением, а не смертностью. Компартментная модель SEIR
    /// без угасания иммунитета даёт каждому заражённому иммунитет навсегда, поэтому
    /// доля умерших ограничена летальностью патогена и не может приблизиться к
    /// единице. Считать победой охват — стандартное решение для жанра: цель достигнута,
    /// когда новых восприимчивых больше не осталось, а умерших сколько удалось.
    /// </remarks>
    public double VictoryInfectionThreshold { get; set; } = 0.95;

    /// <summary>Сколько дней эпидемия должна отсутствовать, чтобы засчитать поражение.</summary>
    public int ExtinctionGraceDays { get; set; } = 30;

    // --- Старт ---

    /// <summary>Доля населения в очаге, где выбирается нулевой пациент.</summary>
    public double PatientZeroFraction { get; set; } = 1e-9;

    /// <summary>Минимальный размер первоначального очага в людях.</summary>
    public double PatientZeroMinimum { get; set; } = 5.0;

    // --- Служебное ---

    /// <summary>Сколько вспышек максимум попадёт в новостную ленту.</summary>
    public int MaxOutbreakReports { get; set; } = 60;

    public SimulationConfig Clone() => (SimulationConfig)MemberwiseClone();
}