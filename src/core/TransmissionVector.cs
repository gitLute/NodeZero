namespace NodeZero.Core;

/// <summary>Пути передачи инфекции. Каждый вектор открывается отдельной мутацией.</summary>
public enum TransmissionVector
{
    /// <summary>Воздушно-капельный. Есть изначально у большинства патогенов.</summary>
    Air = 0,

    /// <summary>Водный. Работает там, где есть открытая вода и жаркий климат.</summary>
    Water = 1,

    /// <summary>Контактный: поверхности, бытовой контакт, торговля. Усиливается урбанизацией.</summary>
    Surface = 2,

    /// <summary>Насекомые-переносчики. Бьёт по тропикам.</summary>
    Insects = 3,

    /// <summary>Зоонозный: от животных к человеку. Зависит от поголовья скота.</summary>
    Animals = 4,
}

/// <summary>Свойства векторов передачи.</summary>
public static class VectorData
{
    public const int Count = 5;

    private static readonly string[] NamesArray =
    {
        "Воздушно-капельный",
        "Водный",
        "Контактный",
        "Насекомые-переносчики",
        "Зоонозный",
    };

    /// <summary>
    /// Масштаб распространения внутри одного региона. Внутри региона контакты
    /// происходят постоянно, поэтому эта составляющая — основная.
    /// </summary>
    public const double IntraRegionScale = 1.0;

    /// <summary>
    /// Коэффициент межрегиональной передачи, сутки⁻¹.
    ///
    /// <para>Это не часть коэффициента передачи внутри региона, а отдельный поток:
    /// вклад соседей в инфекционную нагрузку региона считается независимо от R0.
    /// Так сделано намеренно. Если складывать межрегиональный вклад с внутрирегиональным
    /// в один и тот же множитель, то при типичном окружении (заражены и сам регион, и все
    /// соседи) этот множитель вырастает в разы, и заявленное R0 перестаёт совпадать с
    /// реальным репродуктивным числом. Разделив потоки, мы получаем честное R0 = beta·D_inf
    /// внутри региона и отдельно управляемую скорость продвижения фронта.</para>
    ///
    /// <para>Значение подобрано так, чтобы фронт переходил на соседний регион за
    /// считаные недели, но не перескакивал карту мгновенно.</para>
    /// </summary>
    public const double CrossBorderRate = 0.06;

    public static string Name(TransmissionVector vector) => NamesArray[(int)vector];

    /// <summary>Базовый вес вектора: насколько он эффективен в подходящей среде.</summary>
    public static double BaseWeight(TransmissionVector vector) => vector switch
    {
        TransmissionVector.Air => 1.00,
        TransmissionVector.Water => 0.45,
        TransmissionVector.Surface => 0.85,
        TransmissionVector.Insects => 0.30,
        TransmissionVector.Animals => 0.55,
        _ => 1.0,
    };

    /// <summary>
    /// Приспособленность конкретного региона к вектору. Комбинация климата и свойств
    /// региона: урбанизации, доступности воды, поголовья скота, обитания переносчиков.
    /// </summary>
    public static double Suitability(TransmissionVector vector, HexCell cell) => vector switch
    {
        TransmissionVector.Air =>
            ClimateData.VectorSuitability(cell.Climate, vector) * (0.80 + 0.35 * cell.Urbanization),

        TransmissionVector.Water =>
            ClimateData.VectorSuitability(cell.Climate, vector) * (0.45 + 0.60 * cell.WaterAvailability),

        TransmissionVector.Surface =>
            ClimateData.VectorSuitability(cell.Climate, vector) * (0.30 + 0.95 * cell.Urbanization),

        TransmissionVector.Insects =>
            ClimateData.VectorSuitability(cell.Climate, vector) * cell.VectorHabitat,

        TransmissionVector.Animals =>
            ClimateData.VectorSuitability(cell.Climate, vector) * cell.Livestock,

        _ => 1.0,
    };

    /// <summary>Битовая маска набора векторов — используется как ключ кэша графа контактов.</summary>
    public static int ToMask(TransmissionVector vector) => 1 << (int)vector;
}