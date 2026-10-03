using System;

namespace NodeZero.Core;

/// <summary>Режим работы исследовательских групп, который выбирает игрок.</summary>
public enum ResearchLevel
{
    /// <summary>Никаких исследований. Патоген невозможно секвенировать, но и лекарство не появится.</summary>
    Off = 0,

    /// <summary>Работа идёт вполсилы: замедление обратное плюс меньше прироста.</summary>
    Reduced = 1,

    /// <summary>Полные мощности.</summary>
    Normal = 2,
}

/// <summary>Стадия научного ответа человечества.</summary>
public enum ResearchStage
{
    None = 0,
    Cure = 1,
    Vaccine = 2,
}

/// <summary>
/// Противодействие патогену: прогресс исследований, появление лекарства и вакцины,
/// а также риск секвенирования.
///
/// Никакого случайного « броска кубика»: внимание учёных определяется только
/// числом выявленных случаев и тяжестью болезни, поэтому весь график развития
/// можно просчитать заранее.
/// </summary>
public sealed class ResearchSystem
{
    private const double AttentionReference = 1.0e7;

    public ResearchSystem(SimulationConfig config, double identificationThreshold)
    {
        Config = config;
        IdentificationThreshold = identificationThreshold;
    }

    public SimulationConfig Config { get; }

    public ResearchLevel Setting { get; set; } = ResearchLevel.Normal;

    /// <summary>Накопленный прогресс исследований.</summary>
    public double Progress { get; private set; }

    public ResearchStage Stage { get; private set; } = ResearchStage.None;

    /// <summary>Сколько дней прошло с момента появления лекарства.</summary>
    public double DaysSinceCure { get; private set; }

    /// <summary>
    /// Действующая эффективность лекарства, 0..1. Растёт от нуля после открытия
    /// и снижается устойчивостью патогена.
    /// </summary>
    public double CureEffectiveness { get; private set; }

    /// <summary>Патоген идентифицирован. Не приговор, но после этого исследователи знают,
    /// с чем борются, и работают заметно быстрее.</summary>
    public bool Identified { get; private set; }

    /// <summary>
    /// Сколько суток исследования могут быть остановлены подряд. Дальше группы
    /// выходят из тени сами: полностью отключить науку навсегда нельзя, иначе
    /// стратегия «спрятать патоген и тихо выжечь мир» была бы единственной.
    /// </summary>
    public double DaysResearchSuppressed { get; private set; }

    /// <summary>Число заражённых, при котором патоген идентифицируют.</summary>
    public double IdentificationThreshold { get; }

    /// <summary>Максимальный достигнутый темп прогресса — для отчётов и самопроверки.</summary>
    public double PeakProgressRate { get; private set; }

    /// <summary>Были ли исследования только что принудительно возобновлены.</summary>
    public bool ResearchResumedThisStep { get; private set; }

    /// <summary>Доля прогресса до лекарства, 0..1 — удобно показывать игроку.</summary>
    public double CureProgressRatio => Math.Clamp(Progress / Config.CureThreshold, 0.0, 1.0);

    public double VaccineProgressRatio =>
        Math.Clamp((Progress - Config.CureThreshold) / (Config.VaccineThreshold - Config.CureThreshold), 0.0, 1.0);

    /// <summary>
    /// Годовой шаг исследований.
    /// </summary>
    /// <param name="days">Сколько дней прошло.</param>
    /// <param name="noticeableInfectious">Число инфекционных плюс часть умерших: это то, что видно в новостях.</param>
    /// <param name="severityFactor">Тяжесть патогена: паника ускоряет исследования.</param>
    /// <param name="totalInfectedEver">Сколько людей заразилось за всю партию.</param>
    /// <param name="medicineResistance">Устойчивость патогена к медицине, 0..0.48.</param>
    public void Advance(
        double days,
        double noticeableInfectious,
        double severityFactor,
        double totalInfectedEver,
        double medicineResistance)
    {
        if (!Identified && totalInfectedEver >= IdentificationThreshold)
        {
            Identified = true;
        }

        ResearchResumedThisStep = false;

        if (Setting == ResearchLevel.Off)
        {
            DaysResearchSuppressed += days;
            if (DaysResearchSuppressed >= Config.ResearchSuppressionLimitDays)
            {
                Setting = ResearchLevel.Reduced;
                DaysResearchSuppressed = 0.0;
                ResearchResumedThisStep = true;
            }
        }
        else
        {
            DaysResearchSuppressed = 0.0;
        }

        double gain = Setting switch
        {
            ResearchLevel.Off => 0.0,
            ResearchLevel.Reduced => 0.35,
            _ => 1.0,
        };

        double rate = 0.0;
        if (gain > 0.0 && noticeableInfectious > 0.0 && Stage != ResearchStage.Vaccine)
        {
            // Мягкая нелинейность: пока масштаб мал, прогресс почти не идёт.
            // Это даёт игроку фору в начале и создаёт гонку в середине.
            double normalized = Math.Pow(noticeableInfectious / AttentionReference, 0.45);
            double identificationBonus = Identified ? Config.IdentificationResearchBonus : 1.0;
            rate = Config.ResearchGain * normalized
                   * (1.0 + Config.ResearchSeverityWeight * severityFactor)
                   * gain
                   * identificationBonus;
            Progress += rate * days;
            PeakProgressRate = Math.Max(PeakProgressRate, rate);
        }

        if (Stage == ResearchStage.None && Progress >= Config.CureThreshold)
        {
            Stage = ResearchStage.Cure;
        }
        else if (Stage == ResearchStage.Cure && Progress >= Config.VaccineThreshold)
        {
            Stage = ResearchStage.Vaccine;
        }

        if (Stage == ResearchStage.None)
        {
            return;
        }

        DaysSinceCure += days;
        double ramp = Math.Clamp(DaysSinceCure / Math.Max(1.0, Config.CureDeploymentDays), 0.0, 1.0);
        CureEffectiveness = ramp * (1.0 - medicineResistance);
    }

    /// <summary>Сброс состояния для новой партии с теми же настройками.</summary>
    public void Reset()
    {
        Progress = 0.0;
        Stage = ResearchStage.None;
        DaysSinceCure = 0.0;
        CureEffectiveness = 0.0;
        Identified = false;
        DaysResearchSuppressed = 0.0;
        PeakProgressRate = 0.0;
    }
}