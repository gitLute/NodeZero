using System;
using Godot;
using NodeZero.Core;

namespace NodeZero.App.Ui;

/// <summary>
/// Панель свойств выбранного региона. Показывает всё, на чём держится решение
/// игрока: климат, медицину, плотность контактов, состояние компартментов и то,
/// насколько патогену здесь удобно.
/// </summary>
public partial class HexInspector : PanelContainer
{
    private Label _title = null!;
    private Label _body = null!;
    private SimulationEngine? _engine;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", TopBar.PanelStyle());

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);

        _title = new Label { Text = "Регион не выбран" };
        _title.AddThemeFontSizeOverride("font_size", 16);
        _title.AddThemeColorOverride("font_color", MapPalette.Accent);
        box.AddChild(_title);

        _body = new Label
        {
            Text = "Щёлкните по гексу на карте.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _body.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(_body);
    }

    public void Bind(SimulationEngine engine)
    {
        _engine = engine;
    }

    public void ShowCell(int cellIndex)
    {
        if (_engine is null || cellIndex < 0 || cellIndex >= _engine.Map.Cells.Length)
        {
            _title.Text = "Регион не выбран";
            _body.Text = "Щёлкните по гексу на карте.";
            return;
        }

        HexCell cell = _engine.Map.Cells[cellIndex];
        if (!cell.IsLand)
        {
            _title.Text = $"{cell.Coord} — океан";
            _body.Text = "Здесь нет населения и нет путей передачи: патоген сюда не дойдёт.";
            return;
        }

        double scale = cell.TotalCompartments;
        double infectiousShare = cell.InfectiousFraction * 100.0;
        double deadShare = cell.Dead / Math.Max(1e-9, scale) * 100.0;
        double covered = cell.Susceptible / Math.Max(1e-9, scale) * 100.0;

        _title.Text = $"{cell.Coord} — {ClimateData.Name(cell.Climate)}";

        _body.Text = string.Join(
            System.Environment.NewLine,
            $"Население: {WorldGenerator.FormatPopulation(cell.Population)}",
            $"Медицина: {cell.Healthcare * 100.0:0.#}%",
            $"Городское: {cell.Urbanization * 100.0:0.#}%",
            $"Открытая вода: {cell.WaterAvailability * 100.0:0.#}%",
            $"Переносчики: {cell.VectorHabitat * 100.0:0.#}%",
            $"Скот: {cell.Livestock * 100.0:0.#}%",
            string.Empty,
            $"Не заражены: {covered:0.#}%",
            $"Инфекционные: {WorldGenerator.FormatPopulation(cell.Infectious)} ({infectiousShare:0.###}%)",
            $"Переболели: {WorldGenerator.FormatPopulation(cell.Recovered)}",
            $"Умерли: {WorldGenerator.FormatPopulation(cell.Dead)} ({deadShare:0.#}%)",
            $"Вакцинация: {cell.VaccineCoverage * 100.0:0.#}%",
            string.Empty,
            $"Контактный вес: {_engine.RelativeContactWeight(cellIndex):0.000} (мировое среднее 1.000)",
            $"Восприимчивость к патогену: {_engine.Pathogen.ClimateSusceptibility(cell.Climate) * 100.0:0.#}%");
    }
}

/// <summary>Сводная полоса сверху: дата, мировые показатели, исследование, темп времени.</summary>
public partial class TopBar : PanelContainer
{
    private Label _world = null!;
    private Label _research = null!;
    private Label _status = null!;
    private Label _speedLabel = null!;
    private SimulationEngine? _engine;

    public event System.Action<double>? SpeedChanged;

    public double Speed { get; private set; } = 2.0;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", PanelStyle());

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        margin.AddChild(row);

        _world = MakeLabel(row, 14, MapPalette.TextPrimary);
        _research = MakeLabel(row, 14, MapPalette.Accent);
        _status = MakeLabel(row, 14, MapPalette.Warning);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var pause = MakeButton(row, "Пауза");
        pause.Pressed += () => SetSpeed(Speed > 0.0 ? 0.0 : 2.0);

        _speedLabel = MakeLabel(row, 14, MapPalette.TextMuted);

        foreach (double speed in new[] { 1.0, 3.0, 8.0 })
        {
            double value = speed;
            Button button = MakeButton(row, $"x{(int)speed}");
            button.Pressed += () => SetSpeed(value);
        }

        Button restart = MakeButton(row, "Новая карта");
        restart.Pressed += () => SpeedChanged?.Invoke(-1.0);
    }

    public void Bind(SimulationEngine engine)
    {
        _engine = engine;
    }

    public void SetSpeed(double speed)
    {
        Speed = speed;
        _speedLabel.Text = speed <= 0.0 ? "остановлено" : $"{speed:0.#} сут/с";
        SpeedChanged?.Invoke(speed);
    }

    public void Refresh()
    {
        if (_engine is null)
        {
            return;
        }

        WorldMap map = _engine.Map;
        double coverage = _engine.TotalInfectedEver / Math.Max(1e-9, map.TotalPopulation) * 100.0;

        _world.Text = string.Join(
            "   ",
            $"День {_engine.Day:0} ({_engine.Years:0.00} г.)",
            $"Инфекц. {WorldGenerator.FormatPopulation(_engine.TotalInfectious)}",
            $"Умерло {WorldGenerator.FormatPopulation(_engine.TotalDead)}",
            $"Охват {coverage:0.###}%",
            $"R_t {_engine.InstantaneousReproduction:0.00}",
            $"ДНК {_engine.Pathogen.AvailableDna:0.#}");

        ResearchSystem research = _engine.Research;
        string stage = research.Stage switch
        {
            ResearchStage.Cure => "лекарство разработано",
            ResearchStage.Vaccine => "вакцина в производстве",
            _ => "нет средств",
        };

        _research.Text = string.Join(
            "   ",
            $"Наука {research.Progress:0} / {EngineTargets(_engine)}",
            stage,
            $"эффективность лекарства {research.CureEffectiveness * 100.0:0}%",
            research.Identified ? "патоген идентифицирован" : "патоген неизвестен");

        _status.Text = DescribeStatus(_engine);
        _status.AddThemeColorOverride("font_color", StatusColor(_engine.Status));
    }

    private static string EngineTargets(SimulationEngine engine)
    {
        return engine.Research.Stage switch
        {
            ResearchStage.Cure => engine.Config.VaccineThreshold.ToString("0"),
            _ => engine.Config.CureThreshold.ToString("0"),
        };
    }

    private static string DescribeStatus(SimulationEngine engine) => engine.Status switch
    {
        GameStatus.Running when engine.Research.Stage == ResearchStage.None
                         && engine.Research.Setting == ResearchLevel.Off =>
            "Исследования остановлены — учёные скоро это заметят",
        GameStatus.Running => engine.Research.Stage switch
        {
            ResearchStage.Vaccine => "Вакцина идёт по миру",
            _ => "Мир изучает патоген",
        },
        GameStatus.Victory => "ПОБЕДА: патоген охватил планету",
        _ => "ПОРАЖЕНИЕ: носителей не осталось",
    };

    private static Color StatusColor(GameStatus status) => status switch
    {
        GameStatus.Victory => MapPalette.Success,
        GameStatus.DefeatExtinct => MapPalette.Danger,
        _ => MapPalette.Warning,
    };

    private static Label MakeLabel(Node parent, int fontSize, Color color)
    {
        var label = new Label();
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    private static Button MakeButton(Node parent, string text)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", 13);
        parent.AddChild(button);
        return button;
    }

    internal static StyleBoxFlat PanelStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = MapPalette.PanelBackground,
            BorderColor = MapPalette.PanelBorder,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        return style;
    }
}