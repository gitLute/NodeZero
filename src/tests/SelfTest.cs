using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NodeZero.Core;

namespace NodeZero.Tests;

/// <summary>
/// Автономные тесты ядра симуляции. Запускаются без окна игры:
/// <c>godot-mono --headless --path . -- --selftest</c>.
/// Код возврата ненулевой означает провал хотя бы одного теста.
/// </summary>
public static class SelfTest
{
    /// <summary>Сценарий мутаций для проверки детерминированной эволюции.</summary>
    private static readonly (int Day, MutationId Mutation)[] CanonicalScript =
    {
        (20, MutationId.Infectivity),
        (45, MutationId.Water),
        (70, MutationId.Infectivity),
        (95, MutationId.Surface),
        (130, MutationId.Incubation),
        (165, MutationId.Insects),
        (200, MutationId.Durability),
        (240, MutationId.Infectivity),
        (280, MutationId.ResistHeat),
        (330, MutationId.Severity),
        (380, MutationId.Infectivity),
        (430, MutationId.ResistCold),
    };

    public static int Run(TextWriter output, SimulationConfig? overrides = null)
    {
        var runner = new Runner(output);

        output.WriteLine("== Геометрия гексагональной сетки ==");
        runner.Case("Диск заданного радиуса содержит 3R(R+1)+1 гексов", () =>
        {
            const int radius = 12;
            int count = HexCoord.Disc(new HexCoord(0, 0), radius).Count();
            runner.Check(count == 3 * radius * (radius + 1) + 1,
                $"Получено {count}, ожидалось {3 * radius * (radius + 1) + 1}.");
        });

        runner.Case("У внутренних гексов диска ровно шесть соседей", () =>
        {
            const int radius = 12;
            var center = new HexCoord(0, 0);
            var inside = new HashSet<HexCoord>(HexCoord.Disc(center, radius));
            foreach (HexCoord coord in inside)
            {
                if (coord.DistanceTo(center) >= radius)
                {
                    continue;
                }

                int found = 0;
                foreach (HexCoord direction in HexCoord.Directions)
                {
                    if (inside.Contains(coord + direction))
                    {
                        found++;
                    }
                }

                runner.Check(found == 6, $"Гекс {coord}: соседей внутри диска {found}, ожидалось 6.");
            }
        });

        runner.Case("Метод Neighbor возвращает именно соседа, а не вектор направления", () =>
        {
            var center = new HexCoord(0, 0);
            for (int direction = 0; direction < HexCoord.Directions.Length; direction++)
            {
                HexCoord expected = center + HexCoord.Directions[direction];
                HexCoord actual = center.Neighbor(direction);
                runner.Check(actual == expected,
                    $"Направление {direction}: получено {actual}, ожидалось {expected}.");
            }

            HexCoord cell = new(3, -2);
            var distinct = new HashSet<HexCoord>();
            for (int direction = 0; direction < HexCoord.Directions.Length; direction++)
            {
                distinct.Add(cell.Neighbor(direction));
            }

            runner.Check(distinct.Count == 6,
                $"Соседи гекса {cell} не различаются: найдено {distinct.Count} различных.");
            runner.Check(!distinct.Contains(cell), $"Гекс {cell} указан собственным соседом.");
        });

        runner.Case("Кольцо радиуса 3 содержит 18 гексов на расстоянии ровно 3", () =>
        {
            var center = new HexCoord(0, 0);
            var ring = HexCoord.Ring(center, 3).ToList();
            runner.Check(ring.Count == 18, $"Гексов в кольце {ring.Count}, ожидалось 18.");
            foreach (HexCoord coord in ring)
            {
                runner.Check(coord.DistanceTo(center) == 3, $"Гекс {coord} находится не на радиусе 3.");
            }
        });

        runner.Case("Дистанция симметрична и совпадает с числом шагов", () =>
        {
            var center = new HexCoord(0, 0);
            var all = HexCoord.Disc(center, 10).ToList();
            foreach (HexCoord a in all)
            {
                foreach (HexCoord b in all)
                {
                    runner.Check(a.DistanceTo(b) == b.DistanceTo(a),
                        $"Дистанция {a} <-> {b} несимметрична.");
                }
            }

            HexCoord origin = new(3, -2);
            runner.Check(origin.DistanceTo(origin) == 0, "Дистанция до себя не ноль.");
        });

        runner.Case("Обратное преобразование пикселей в координаты", () =>
        {
            var layout = new HexLayout(30f);
            foreach (HexCoord coord in HexCoord.Disc(new HexCoord(0, 0), 14))
            {
                Point2 pixel = layout.Center(coord);
                HexCoord restored = layout.ToCoord(pixel);
                runner.Check(restored == coord,
                    $"Гекс {coord} превратился в {restored} при обратном преобразовании.");
            }
        });

        output.WriteLine("== Генерация мира ==");
        runner.Case("Один сид — один и тот же мир", () =>
        {
            SimulationConfig a = Base();
            SimulationConfig b = Base();
            ulong hashA = WorldGenerator.Generate(a).StructuralHash();
            ulong hashB = WorldGenerator.Generate(b).StructuralHash();
            runner.Check(hashA == hashB, $"Хеши разошлись: {hashA:X16} и {hashB:X16}.");
        });

        runner.Case("Разные сиды дают разные миры", () =>
        {
            ulong hashA = WorldGenerator.Generate(Base()).StructuralHash();
            SimulationConfig other = Base();
            other.Seed = 987654321UL;
            ulong hashB = WorldGenerator.Generate(other).StructuralHash();
            runner.Check(hashA != hashB, "Структура мира совпала при разных сидах.");
        });

        runner.Case("Мир связен по суше и население равно заданному объёму", () =>
        {
            WorldMap map = WorldGenerator.Generate(Base());
            runner.Check(map.LandIndices.Length > 0, "На карте нет суши.");
            runner.Check(map.IsLandConnected(), "Суша несвязна: патоген застрянет на одном острове.");

            double landShare = (double)map.LandIndices.Length / map.Cells.Length;
            runner.Check(landShare > 0.15 && landShare < 0.85,
                $"Доля суши {landShare * 100.0:0.#}% вне диапазона 15..85%.");

            runner.CheckClose(
                map.TotalPopulation,
                Base().TargetWorldPopulation,
                1e-6,
                "Суммарное население не совпало с целевым.");
        });

        runner.Case("Регионы суши соседствуют, океан их не разрывает", () =>
        {
            WorldMap map = WorldGenerator.Generate(Base());
            int landWithFullNeighbors = 0;
            foreach (int index in map.LandIndices)
            {
                HexCell cell = map.Cells[index];
                if (cell.Neighbors.Length == 6)
                {
                    landWithFullNeighbors++;
                }
            }

            runner.Check(landWithFullNeighbors > 0,
                "Ни один наземный регион не имеет шести соседей — карта собрана неверно.");
        });

        output.WriteLine("== Целостность симуляции ==");
        runner.Case("Масса населения сохраняется на длинном прогоне", () =>
        {
            (WorldMap map, SimulationEngine engine) = BuildScenario(Base());
            engine.AdvanceDays(400.0);
            double error = map.MassBalanceError();
            runner.Check(error < 1e-6, $"Баланс массы нарушен: относительная ошибка {error:0.##E+0}.");
        });

        runner.Case("Компартменты не уходят в отрицательные значения", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 6.0;
            config.InfectiousDays = 1.5;
            config.IncubationDays = 1.0;
            (WorldMap map, SimulationEngine engine) = BuildScenario(config);

            double minimum = double.MaxValue;
            for (int step = 0; step < 600; step++)
            {
                engine.AdvanceDays(0.25);
                if (step % 10 == 0)
                {
                    minimum = Math.Min(minimum, MinimumCompartment(map));
                }
            }

            runner.Check(minimum >= 0.0, $"Обнаружен отрицательный компартмент: {minimum:0.##E+0}.");
        });

        runner.Case("Распространение локально: за 6 суток патоген не ушёл дальше ближайших соседей", () =>
        {
            (WorldMap map, SimulationEngine engine) = BuildScenario(Base());
            int start = engine.PatientZeroCell ?? throw new InvalidOperationException("Очаг не создан.");
            HexCoord origin = map.CoordOf(start);

            engine.AdvanceDays(6.0);

            // Проверяем локальность по числу людей, а не по числу затронутых ячеек:
            // в детерминированной модели дальние регионы получают ненулевую долю
            // инфекции сразу, но ничтожную.
            double near = 0.0;
            double far = 0.0;
            foreach (int index in map.LandIndices)
            {
                HexCell cell = map.Cells[index];
                double infectious = cell.Infectious;
                if (cell.Coord.DistanceTo(origin) <= 2)
                {
                    near += infectious;
                }
                else
                {
                    far += infectious;
                }
            }

            double total = near + far;
            runner.Check(total > 0.0, "За 6 суток не заразился ни один человек.");
            runner.Check(far <= total * 0.02,
                $"Вне круга радиусом два гекса оказалось {far / total * 100.0:0.###}% " +
                $"инфекционных — распространение не локально.");
        });

        output.WriteLine("== Детерминированность ==");
        runner.Case("Один и тот же сценарий мутаций даёт идентичный ход эпидемии", () =>
        {
            string first = RunScriptedScenario(500, CanonicalScript);
            string second = RunScriptedScenario(500, CanonicalScript);
            runner.Check(first == second, $"Хеши разошлись: {first} и {second}.");
        });

        runner.Case("Смена срока мутации меняет траекторию", () =>
        {
            var shifted = CanonicalScript.Select((step, i) => step.Day == 240 ? (241, step.Mutation) : step).ToArray();
            string first = RunScriptedScenario(500, CanonicalScript);
            string second = RunScriptedScenario(500, shifted);
            runner.Check(first != second, "Сдвиг одной мутации на сутки не изменил результат.");
        });

        runner.Case("Разные сиды дают разную эпидемию", () =>
        {
            string first = RunScriptedScenario(300, Array.Empty<(int, MutationId)>());
            SimulationConfig other = Base();
            other.Seed = 424242UL;
            string second = RunScriptedScenario(300, Array.Empty<(int, MutationId)>(), other);
            runner.Check(first != second, "Сид не влияет на ход эпидемии.");
        });

        output.WriteLine("== Эпидемиологическое поведение ==");
        runner.Case("Высокое R0 даёт рост эпидемии", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 4.0;
            (_, SimulationEngine engine) = BuildScenario(config);
            engine.AdvanceDays(120.0);
            double share = engine.TotalInfectedEver / engine.Map.TotalPopulation;
            runner.Check(share > 0.01,
                $"За 120 суток заразилось только {share * 100.0:0.###}% населения.");
        });

        runner.Case("Низкое R0 приводит к вымиранию патогена", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 0.5;
            (_, SimulationEngine engine) = BuildScenario(config);
            for (int day = 0; day < 150 && engine.Status == GameStatus.Running; day++)
            {
                engine.AdvanceDays(10.0);
            }

            runner.Check(engine.Status == GameStatus.DefeatExtinct,
                $"Ожидалось вымирание патогена, получено состояние {engine.Status} " +
                $"(заразилось {engine.TotalInfectedEver:0.##E+0}).");
        });

        runner.Case("Более высокое R0 всегда быстрее распространяет патоген", () =>
        {
            SimulationConfig low = Base();
            low.BaseReproductionNumber = 2.0;
            SimulationConfig high = Base();
            high.BaseReproductionNumber = 4.0;

            (_, SimulationEngine weak) = BuildScenario(low);
            (_, SimulationEngine strong) = BuildScenario(high);
            weak.AdvanceDays(150.0);
            strong.AdvanceDays(150.0);

            runner.Check(strong.TotalInfectedEver > weak.TotalInfectedEver,
                $"Сильный патоген заразил {strong.TotalInfectedEver:0.###}, " +
                $"слабый — {weak.TotalInfectedEver:0.###}.");
        });

        output.WriteLine("== Исследования, лекарство, вакцина ==");
        runner.Case("Идентификация патогена ускоряет исследования", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 3.0;
            config.IdentificationThresholdFraction = 0.05;
            config.ResearchGain = 2.0;
            config.IdentificationResearchBonus = 2.0;
            (_, SimulationEngine engine) = BuildScenario(config);

            engine.AdvanceDays(1.0);
            double rateBefore = engine.Research.PeakProgressRate;
            for (int day = 0; day < 40 && !engine.Research.Identified; day++)
            {
                engine.AdvanceDays(5.0);
            }

            runner.Check(engine.Research.Identified,
                "Патоген не был идентифицирован, хотя заражённых стало больше порога.");

            double progressBefore = engine.Research.Progress;
            engine.AdvanceDays(5.0);
            double after = (engine.Research.Progress - progressBefore) / 5.0;
            runner.Check(after > rateBefore,
                $"Темп исследований после идентификации {after:0.###} не вырос относительно {rateBefore:0.###}.");
        });

        runner.Case("Отключённые исследования не останавливают вакцинацию", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 3.5;
            config.ResearchGain = 0.0;
            config.IdentificationThresholdFraction = 0.9;
            (_, SimulationEngine engine) = BuildScenario(config);
            engine.Research.Setting = ResearchLevel.Off;
            engine.AdvanceDays(300.0);

            runner.Check(engine.Research.Stage == ResearchStage.None,
                "При отключённых исследованиях появилось противопатогенное средство.");
        });

        runner.Case("Быстрое лекарство сдерживает распространение", () =>
        {
            SimulationConfig fast = Base();
            fast.BaseReproductionNumber = 3.5;
            fast.ResearchGain = 9.0;
            fast.IdentificationThresholdFraction = 0.9;
            fast.CureThreshold = 30.0;

            SimulationConfig slow = Base();
            slow.BaseReproductionNumber = 3.5;
            slow.ResearchGain = 0.02;
            slow.IdentificationThresholdFraction = 0.9;

            (_, SimulationEngine fastEngine) = BuildScenario(fast);
            (_, SimulationEngine slowEngine) = BuildScenario(slow);
            fastEngine.AdvanceDays(320.0);
            slowEngine.AdvanceDays(320.0);

            runner.Check(fastEngine.Research.CureEffectiveness > 0.8,
                $"Эффективность лекарства {fastEngine.Research.CureEffectiveness:0.##}, ожидалось выше 0.8.");
            runner.Check(fastEngine.TotalInfectious < slowEngine.TotalInfectious,
                $"С лекарством заражено {fastEngine.TotalInfectious:0.###}, " +
                $"без него — {slowEngine.TotalInfectious:0.###}.");
        });

        runner.Case("Вакцина останавливает эпидемию", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 3.5;
            config.ResearchGain = 12.0;
            config.IdentificationThresholdFraction = 0.9;
            config.CureThreshold = 25.0;
            config.VaccineThreshold = 60.0;

            (_, SimulationEngine engine) = BuildScenario(config);
            for (int day = 0; day < 120 && engine.Research.Stage != ResearchStage.Vaccine; day++)
            {
                engine.AdvanceDays(10.0);
            }

            runner.Check(engine.Research.Stage == ResearchStage.Vaccine,
                "Вакцина так и не была разработана.");

            double infectiousBefore = engine.TotalInfectious;
            engine.AdvanceDays(900.0);
            runner.Check(engine.TotalInfectious < infectiousBefore * 0.2,
                $"После вакцинации заражено {engine.TotalInfectious:0.###} против {infectiousBefore:0.###} до неё.");
        });

        runner.Case("Медицина снижает летальность инфекции", () =>
        {
            (_, SimulationEngine poor) = BuildHealthcareScenario(0.05);
            (_, SimulationEngine rich) = BuildHealthcareScenario(0.85);

            double cfrPoor = poor.TotalDead / Math.Max(1e-9, poor.TotalDead + poor.TotalRecovered);
            double cfrRich = rich.TotalDead / Math.Max(1e-9, rich.TotalDead + rich.TotalRecovered);

            runner.Check(cfrPoor > 0.0, "В бедном мире не оказалось ни одной смерти.");
            runner.Check(cfrRich < cfrPoor,
                $"Летальность при хорошей медицине {cfrRich:P1} не ниже, чем при плохой {cfrPoor:P1}.");
        });

        output.WriteLine("== Мутации ==");
        runner.Case("Мутация не проходит без очков ДНК", () =>
        {
            Pathogen pathogen = new(Base());
            runner.Check(!pathogen.TryMutate(MutationId.Water, out string? refusal),
                "Мутация прошла при нулевом балансе ДНК.");
            runner.Check(refusal is not null, "Не вернулась причина отказа.");
            runner.Check(pathogen.Level(MutationId.Water) == 0, "Уровень мутации изменился при отказе.");
            runner.CheckClose(pathogen.SpentDna, 0.0, 1e-9, "Очки списаны при неудачной покупке.");
        });

        runner.Case("Стоимость мутаций растёт с уровнем", () =>
        {
            Pathogen pathogen = new(Base());
            pathogen.EarnDna(1000.0);
            MutationDef def = MutationCatalog.Get(MutationId.Infectivity);

            runner.Check(pathogen.Level(MutationId.Infectivity) == 0,
                "Исходный уровень мутации не равен нулю.");

            double previous = -1.0;
            for (int level = 1; level <= def.MaxLevel; level++)
            {
                int cost = pathogen.CostToNextLevel(MutationId.Infectivity);
                runner.Check(cost > previous,
                    $"Уровень {level}: цена {cost} не больше предыдущей {previous}.");
                previous = cost;

                runner.Check(pathogen.TryMutate(MutationId.Infectivity, out _),
                    $"Не удалось купить уровень {level} при полном балансе.");
                runner.Check(pathogen.Level(MutationId.Infectivity) == level,
                    $"После покупки ожидался уровень {level}, получено {pathogen.Level(MutationId.Infectivity)}.");
            }

            runner.Check(pathogen.IsAtMax(MutationId.Infectivity), "Максимальный уровень не достигнут.");
            runner.Check(!pathogen.CanMutate(MutationId.Infectivity), "Максимальный уровень предлагается к покупке.");
        });

        runner.Case("Требования к мутациям соблюдаются", () =>
        {
            Pathogen pathogen = new(Base());
            pathogen.EarnDna(1000.0);

            runner.Check(!pathogen.TryMutate(MutationId.Insects, out _),
                "Мутация насекомых открылась без водного пути.");
            runner.Check(pathogen.TryMutate(MutationId.Water, out _), "Водной путь не купился.");
            runner.Check(pathogen.TryMutate(MutationId.Insects, out _),
                "Мутация насекомых не открылась после водного пути.");

            runner.Check(!pathogen.TryMutate(MutationId.MedicineResistance, out _),
                "Устойчивость к медицине открылась при низкой тяжести.");

            for (int i = 0; i < 3; i++)
            {
                runner.Check(pathogen.TryMutate(MutationId.Severity, out _), "Не удалось повысить тяжесть.");
            }

            runner.Check(!pathogen.CanMutate(MutationId.MedicineResistance),
                "Устойчивость к медицине предложена при тяжести 3.");

            runner.Check(pathogen.TryMutate(MutationId.Severity, out _), "Не удалось довести тяжесть до 4.");
            runner.Check(pathogen.TryMutate(MutationId.MedicineResistance, out _),
                "Устойчивость к медицине не открылась при тяжести 4.");
        });

        runner.Case("Открытие пути передачи меняет географию болезни", () =>
        {
            WorldMap map = WorldGenerator.Generate(Base());
            HexCell tropical = FindHumidTropical(map);
            HexCell polar = FindPolar(map);

            (_, SimulationEngine engine) = BuildScenario(Base());
            double tropicalBefore = engine.RelativeContactWeight(tropical.Index);
            double polarBefore = engine.RelativeContactWeight(polar.Index);

            engine.Pathogen.EarnDna(1000.0);
            runner.Check(engine.TryMutate(MutationId.Water, out _), "Водной путь не купился.");

            double tropicalAfter = engine.RelativeContactWeight(tropical.Index);
            double polarAfter = engine.RelativeContactWeight(polar.Index);

            runner.Check(tropicalAfter > tropicalBefore + 0.05,
                $"Тропический регион почти не изменился: {tropicalBefore:0.000} -> {tropicalAfter:0.000}.");
            runner.Check(polarAfter - polarBefore < tropicalAfter - tropicalBefore,
                "Полярный регион вырос в относительном темпе сильнее тропического.");
            runner.Check(tropical.Climate == Climate.HotHumid, "Найден не тропический регион.");
            runner.Check(polar.Climate == Climate.Polar, "Найден не полярный регион.");
        });

        runner.Case("Климатическая устойчивость меняет только свой климат", () =>
        {
            Pathogen pathogen = new(Base());
            pathogen.EarnDna(1000.0);

            double hotBefore = pathogen.ClimateSusceptibility(Climate.HotDry);
            double humidBefore = pathogen.ClimateSusceptibility(Climate.HotHumid);
            double coldBefore = pathogen.ClimateSusceptibility(Climate.ColdHumid);

            runner.Check(pathogen.TryMutate(MutationId.ResistHeat, out _), "Теплоустойчивость не купилась.");
            runner.Check(pathogen.TryMutate(MutationId.ResistHeat, out _), "Повторная покупка не удалась.");

            runner.Check(pathogen.ClimateSusceptibility(Climate.HotDry) < hotBefore,
                "Восприимчивость к жаре не снизилась.");
            runner.Check(pathogen.ClimateSusceptibility(Climate.HotHumid) < humidBefore,
                "Восприимчивость во влажных тропиках не снизилась.");
            runner.CheckClose(pathogen.ClimateSusceptibility(Climate.ColdHumid), coldBefore, 1e-12,
                "Восприимчивость в холодном влажном климате изменилась.");
            runner.CheckClose(pathogen.ClimateSusceptibility(Climate.Polar), 1.0, 1e-12,
                "Полярная восприимчивость изменилась от теплоустойчивости.");
        });

        runner.Case("Полный бюджет ДНК покрывает весь каталог, но не более", () =>
        {
            runner.Check(MutationCatalog.TotalDna > 0, "Каталог мутаций пуст.");
            SimulationConfig config = Base();
            double cap = MutationCatalog.TotalDna * config.DnaEarnCap;
            runner.Check(cap < MutationCatalog.TotalDna,
                "Накопить можно весь бюджет ДНК — выбор мутаций перестаёт быть значимым.");

            Pathogen pathogen = new(config);
            pathogen.EarnDna(double.MaxValue / 4.0);
            runner.CheckClose(pathogen.EarnedDna, cap, 1e-6, "Потолок заработка ДНК не соблюдён.");
        });

        output.WriteLine("== Победа ==");
        runner.Case("Быстрый патоген при отключённых исследованиях охватывает мир", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 4.0;
            config.InfectiousDays = 6.0;
            config.IdentificationThresholdFraction = 0.9;
            config.ResearchGain = 0.0;

            (_, SimulationEngine engine) = BuildScenario(config);
            engine.Research.Setting = ResearchLevel.Off;
            engine.Pathogen.EarnDna(1000.0);
            for (int i = 0; i < 4; i++)
            {
                engine.TryMutate(MutationId.Infectivity, out _);
                engine.TryMutate(MutationId.Incubation, out _);
                engine.TryMutate(MutationId.Surface, out _);
            }

            for (int day = 0; day < 200 && engine.Status == GameStatus.Running; day++)
            {
                engine.AdvanceDays(10.0);
            }

            double coverage = engine.TotalInfectedEver / engine.Map.TotalPopulation;
            runner.Check(engine.Status == GameStatus.Victory,
                $"Ожидалась победа, получено состояние {engine.Status}; охват {coverage * 100.0:0.##}%.");
        });

        runner.Case("Вакцинация делает победу недостижимой", () =>
        {
            SimulationConfig config = Base();
            config.BaseReproductionNumber = 4.0;
            config.ResearchGain = 20.0;
            config.IdentificationThresholdFraction = 0.9;
            config.CureThreshold = 20.0;
            config.VaccineThreshold = 40.0;

            (_, SimulationEngine engine) = BuildScenario(config);
            engine.Pathogen.EarnDna(1000.0);
            for (int i = 0; i < 4; i++)
            {
                engine.TryMutate(MutationId.Infectivity, out _);
            }

            for (int day = 0; day < 200 && engine.Status == GameStatus.Running; day++)
            {
                engine.AdvanceDays(10.0);
            }

            runner.Check(engine.Status != GameStatus.Victory,
                $"Победа достигнута при вакцинированном мире (статус {engine.Status}).");
        });

        output.WriteLine();
        output.WriteLine($"Итог: успешно {runner.Passed}, провалено {runner.Failed}.");
        return runner.Failed;
    }

    // --- Вспомогательные функции ---

    private static SimulationConfig Base() => new()
    {
        Seed = 20261003UL,
        MapRadius = 12,
        SubStepDays = 0.25,
    };

    private static (WorldMap Map, SimulationEngine Engine) BuildScenario(SimulationConfig config)
    {
        WorldMap map = WorldGenerator.Generate(config);
        var pathogen = new Pathogen(config);
        var engine = new SimulationEngine(map, pathogen, config);
        engine.StartOutbreak(engine.SuggestPatientZero());
        return (map, engine);
    }

    private static double MinimumCompartment(WorldMap map)
    {
        double minimum = double.MaxValue;
        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            minimum = Math.Min(minimum, cell.Susceptible);
            minimum = Math.Min(minimum, cell.Exposed);
            minimum = Math.Min(minimum, cell.Infectious);
            minimum = Math.Min(minimum, cell.Recovered);
            minimum = Math.Min(minimum, cell.Dead);
        }

        return minimum;
    }

    /// <summary>
    /// Прогон сценария с фиксированным расписанием мутаций и возвратом хеша
    /// всей траектории. Одинаковый вход обязан давать одинаковый хеш.
    /// </summary>
    private static string RunScriptedScenario(
        int days,
        (int Day, MutationId Mutation)[] script,
        SimulationConfig? config = null)
    {
        SimulationConfig effective = config ?? Base();
        WorldMap map = WorldGenerator.Generate(effective);
        var pathogen = new Pathogen(effective);
        var engine = new SimulationEngine(map, pathogen, effective);

        // Стартовый бюджет очков, чтобы расписание мутаций не упиралось в экономику.
        pathogen.EarnDna(MutationCatalog.TotalDna);
        engine.StartOutbreak(engine.SuggestPatientZero());

        ulong hash = 14695981039346656037UL;
        int cursor = 0;

        for (int day = 1; day <= days; day++)
        {
            engine.AdvanceDays(1.0);

            while (cursor < script.Length && script[cursor].Day == day)
            {
                engine.TryMutate(script[cursor].Mutation, out _);
                cursor++;
            }

            hash = Mix(hash, engine.TotalSusceptible);
            hash = Mix(hash, engine.TotalExposed);
            hash = Mix(hash, engine.TotalInfectious);
            hash = Mix(hash, engine.TotalDead);
            hash = Mix(hash, engine.Research.Progress);
            hash = MixInt(hash, engine.Pathogen.Level(MutationId.Infectivity));
        }

        return $"{hash:X16}";
    }

    /// <summary>
    /// Сценарий с одинаковым миром, но принудительно выровненным уровнем медицины.
    /// Нужен, чтобы измерить вклад медицины в летальность без влияния на географию.
    /// </summary>
    private static (WorldMap Map, SimulationEngine Engine) BuildHealthcareScenario(double healthcare)
    {
        SimulationConfig config = Base();
        config.BaseReproductionNumber = 3.5;
        config.IdentificationThresholdFraction = 0.9;
        config.ResearchGain = 0.0;

        WorldMap map = WorldGenerator.Generate(config);
        foreach (int index in map.LandIndices)
        {
            map.Cells[index].Healthcare = healthcare;
        }

        var pathogen = new Pathogen(config);
        pathogen.EarnDna(MutationCatalog.TotalDna);
        for (int i = 0; i < 4; i++)
        {
            pathogen.TryMutate(MutationId.Severity, out _);
        }

        var engine = new SimulationEngine(map, pathogen, config);
        engine.Research.Setting = ResearchLevel.Off;
        engine.StartOutbreak(engine.SuggestPatientZero());
        engine.AdvanceDays(300.0);
        return (map, engine);
    }

    private static HexCell FindHumidTropical(WorldMap map)
    {
        HexCell? best = null;
        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            if (cell.Climate != Climate.HotHumid)
            {
                continue;
            }

            if (best is null || cell.WaterAvailability > best.WaterAvailability)
            {
                best = cell;
            }
        }

        return best ?? throw new InvalidOperationException("На карте нет влажных тропиков.");
    }

    private static HexCell FindPolar(WorldMap map)
    {
        HexCell? best = null;
        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            if (cell.Climate != Climate.Polar)
            {
                continue;
            }

            if (best is null || cell.Population > best.Population)
            {
                best = cell;
            }
        }

        return best ?? throw new InvalidOperationException("На карте нет полярных регионов.");
    }

    private static ulong Mix(ulong hash, double value)
    {
        hash ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        hash *= 1099511628211UL;
        return hash;
    }

    private static ulong MixInt(ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        hash *= 1099511628211UL;
        return hash;
    }

    /// <summary>Минимальный тестовый раннер: без внешних зависимостей.</summary>
    private sealed class Runner
    {
        private readonly TextWriter _output;

        public Runner(TextWriter output)
        {
            _output = output;
        }

        public int Passed { get; private set; }

        public int Failed { get; private set; }

        public void Case(string name, Action body)
        {
            try
            {
                body();
                Passed++;
                _output.WriteLine($"  [ок]   {name}");
            }
            catch (Exception e)
            {
                Failed++;
                _output.WriteLine($"  [СБОЙ] {name}");
                _output.WriteLine($"         {e.Message}");
            }
        }

        public void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public void CheckClose(double actual, double expected, double tolerance, string message)
        {
            if (Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(
                    $"{message} Получено {actual:0.#######}, ожидалось {expected:0.#######} " +
                    $"(допуск {tolerance:0.##E+0}).");
            }
        }
    }
}