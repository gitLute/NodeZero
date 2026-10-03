namespace NodeZero.Core;

/// <summary>Климатические зоны региона. Определяют, какие пути передачи возможны.</summary>
public enum Climate
{
    /// <summary>Полярный пояс. Практически нет переносчиков, вода холодная.</summary>
    Polar = 0,

    /// <summary>Холодный сухой пояс. Степи и тундра.</summary>
    ColdDry = 1,

    /// <summary>Холодный влажный пояс. Тайга.</summary>
    ColdHumid = 2,

    /// <summary>Жаркий сухой пояс. Пустыни: вода как источник заражения слабо работает.</summary>
    HotDry = 3,

    /// <summary>Жаркий влажный пояс. Тропики: комары, стоячие воды.</summary>
    HotHumid = 4,
}

/// <summary>Свойства климатических зон.</summary>
public static class ClimateData
{
    public const int Count = 5;

    private static readonly string[] NamesArray =
    {
        "Полярный",
        "Холодный сухой",
        "Холодный влажный",
        "Жаркий сухой",
        "Жаркий влажный",
    };

    public static string Name(Climate climate) => NamesArray[(int)climate];

    /// <summary>
    /// Пригодность климата для жизни населения: от него зависит и плотность
    /// населения, и базовая восприимчивость к болезни.
    /// </summary>
    public static double Habitability(Climate climate) => climate switch
    {
        Climate.Polar => 0.10,
        Climate.ColdDry => 0.45,
        Climate.ColdHumid => 0.80,
        Climate.HotDry => 0.32,
        Climate.HotHumid => 0.78,
        _ => 0.5,
    };

    /// <summary>
    /// Множитель приспособленности среды к вектору передачи. Это главный механизм,
    /// который делает карту стратегически разной: один и тот же патоген в тропиках
    /// и в пустыне ведёт себя принципиально по-разному.
    /// </summary>
    public static double VectorSuitability(Climate climate, TransmissionVector vector) => (climate, vector) switch
    {
        (Climate.Polar, TransmissionVector.Air) => 0.85,
        (Climate.ColdDry, TransmissionVector.Air) => 0.90,
        (Climate.ColdHumid, TransmissionVector.Air) => 0.95,
        (Climate.HotDry, TransmissionVector.Air) => 0.88,
        (Climate.HotHumid, TransmissionVector.Air) => 0.90,

        (Climate.Polar, TransmissionVector.Water) => 0.10,
        (Climate.ColdDry, TransmissionVector.Water) => 0.15,
        (Climate.ColdHumid, TransmissionVector.Water) => 0.40,
        (Climate.HotDry, TransmissionVector.Water) => 0.22,
        (Climate.HotHumid, TransmissionVector.Water) => 0.95,

        (Climate.Polar, TransmissionVector.Surface) => 0.60,
        (Climate.ColdDry, TransmissionVector.Surface) => 0.80,
        (Climate.ColdHumid, TransmissionVector.Surface) => 0.90,
        (Climate.HotDry, TransmissionVector.Surface) => 0.75,
        (Climate.HotHumid, TransmissionVector.Surface) => 0.85,

        (Climate.Polar, TransmissionVector.Insects) => 0.02,
        (Climate.ColdDry, TransmissionVector.Insects) => 0.05,
        (Climate.ColdHumid, TransmissionVector.Insects) => 0.28,
        (Climate.HotDry, TransmissionVector.Insects) => 0.32,
        (Climate.HotHumid, TransmissionVector.Insects) => 1.20,

        (Climate.Polar, TransmissionVector.Animals) => 0.25,
        (Climate.ColdDry, TransmissionVector.Animals) => 0.75,
        (Climate.ColdHumid, TransmissionVector.Animals) => 0.60,
        (Climate.HotDry, TransmissionVector.Animals) => 0.45,
        (Climate.HotHumid, TransmissionVector.Animals) => 0.70,

        _ => 1.0,
    };
}