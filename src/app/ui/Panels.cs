using System;
using System.Collections.Generic;
using Godot;
using NodeZero.Core;

namespace NodeZero.App.Ui;

/// <summary>
/// Верхняя полоса: мировые показатели, состояние науки и управление темпом времени.
///
/// <para>Полоса собрана вручную, без контейнеров, и именно поэтому она не «скачет».
/// Обычные HBox- и GridContainer пересчитывают ширины от текста: стоит числу
/// вырасти на два знака, как все соседние элементы сдвигаются. Здесь у каждой
/// подписи фиксированная позиция и ширина, текст обрезается многоточием, а сама
/// подпись переписывается только при реальном изменении значения и не чаще
/// заданной частоты.</para>
/// </summary>
public partial class TopBar : Panel
{
	private const float StatsLeft = 12.0f;
	private const float BarHeight = 46.0f;
	private const double BaseSpeed = 2.0;

	private Label _date = null!;
	private Label _infectious = null!;
	private Label _dead = null!;
	private Label _coverage = null!;
	private Label _rt = null!;
	private Label _dna = null!;
	private Label _science = null!;
	private Label _status = null!;
	private Button _pause = null!;
	private Button _start = null!;

	private SimulationEngine? _engine;
	private double _speed = 2.0;
	private bool _startVisible;
	private double _lastUpdateSeconds;

	/// <summary>Скорость в сутках на реальную секунду.</summary>
	public double Speed => _speed;

	/// <summary>Событие темпа времени: значение меньше нуля означает «новая карта».</summary>
	public event System.Action<double>? SpeedChanged;

	/// <summary>Событие «выбрать случайный старт».</summary>
	public event System.Action? RandomStartRequested;

	public override void _Ready()
	{
		AddThemeStyleboxOverride("panel", PanelStyle());
		CustomMinimumSize = new Vector2(0, BarHeight);
		MouseFilter = MouseFilterEnum.Ignore;

		_date = AddStat(StatsLeft, 0.0f, 96.0f, "День 0", 13, MapPalette.TextPrimary);
		_infectious = AddStat(StatsLeft + 100.0f, 0.0f, 116.0f, "Инф. 0", 13, MapPalette.Danger);
		_dead = AddStat(StatsLeft + 222.0f, 0.0f, 104.0f, "Умерло 0", 13, MapPalette.TextMuted);
		_coverage = AddStat(StatsLeft + 330.0f, 0.0f, 92.0f, "Охват 0%", 13, MapPalette.TextPrimary);
		_rt = AddStat(StatsLeft + 426.0f, 0.0f, 74.0f, "R_t 0", 13, MapPalette.Accent);
		_dna = AddStat(StatsLeft + 504.0f, 0.0f, 88.0f, "ДНК 0", 13, MapPalette.Success);

		_science = AddStat(StatsLeft + 600.0f, 3.0f, 300.0f, string.Empty, 11, MapPalette.Accent);
		_status = AddStat(StatsLeft + 600.0f, 21.0f, 300.0f, string.Empty, 13, MapPalette.Warning);

		// Порядок создания обратен порядку слева направо: кнопки раскладываются
		// от правого края, поэтому первая созданная оказывается самой левой.
		_start = AddButton("Случайный старт", 118.0f, () => RandomStartRequested?.Invoke());
		_start.Visible = false;

		_pause = AddButton("Пауза", 78.0f, () => SetSpeed(_speed > 0.0 ? 0.0 : BaseSpeed));
		AddButton("x1", 40.0f, () => SetSpeed(1.0));
		AddButton("x3", 40.0f, () => SetSpeed(3.0));
		AddButton("x8", 40.0f, () => SetSpeed(8.0));
		AddButton("Новая карта", 104.0f, () => SpeedChanged?.Invoke(-1.0));

		Relayout();
	}

	/// <summary>Показать или спрятать кнопку случайного старта.</summary>
	public void SetStartButtonVisible(bool visible)
	{
		_startVisible = visible;
		_start.Visible = visible;
		Relayout();
	}

	public void Bind(SimulationEngine engine)
	{
		_engine = engine;
		_lastUpdateSeconds = -1.0;
		SetSpeed(_speed);
		Refresh(force: true);
	}

	public void SetSpeed(double speed)
	{
		_speed = speed;
		if (_pause is not null)
		{
			_pause.Text = speed > 0.0 ? "Пауза" : "Продолжить";
		}

		SpeedChanged?.Invoke(speed);
		Refresh(force: true);
	}

	/// <summary>
	/// Обновить показатели. Значения округляются до читаемых величин, а подписи
	/// переписываются не чаще шести раз в секунду — иначе числа мельтешат.
	/// </summary>
	public void Refresh(bool force = false, double nowSeconds = 0.0)
	{
		if (_engine is null)
		{
			return;
		}

		if (!force)
		{
			if (_engine.Status != GameStatus.Running && _status.Text.Length == 0)
			{
				return;
			}

			if (nowSeconds - _lastUpdateSeconds < 0.16)
			{
				return;
			}
		}

		_lastUpdateSeconds = nowSeconds;

		SimulationEngine engine = _engine;
		double coverage = engine.TotalInfectedEver / Math.Max(1e-9, engine.Map.TotalPopulation) * 100.0;

		SetText(_date, $"День {engine.Day:0} · {engine.Years:0.00} г");
		SetText(_infectious, $"Инф. {WorldGenerator.FormatPopulation(engine.TotalInfectious)}");
		SetText(_dead, $"Умерло {WorldGenerator.FormatPopulation(engine.TotalDead)}");
		SetText(_coverage, $"Охват {coverage:0.##}%");
		SetText(_rt, $"R_t {engine.InstantaneousReproduction:0.00}");
		SetText(_dna, $"ДНК {engine.Pathogen.AvailableDna:0.#}");

		SetText(_science,
			$"Наука {engine.Research.Progress:0} / {NextTarget(engine)}   " +
			$"лекарство {engine.Research.CureEffectiveness * 100.0:0}%   " +
			$"{(engine.Research.Identified ? "патоген известен науке" : "патоген не идентифицирован")}");

		string status = DescribeStatus(engine);
		SetText(_status, status);
		_status.AddThemeColorOverride("font_color", StatusColor(engine.Status));
	}

	/// <summary>Заставить полосу перерисоваться немедленно.</summary>
	public void Invalidate() => _lastUpdateSeconds = -1.0;

	private static string NextTarget(SimulationEngine engine) =>
		engine.Research.Stage == ResearchStage.None
			? engine.Config.CureThreshold.ToString("0")
			: engine.Config.VaccineThreshold.ToString("0");

	private static string DescribeStatus(SimulationEngine engine) => engine.Status switch
	{
		GameStatus.Victory => "ПОБЕДА: патоген охватил планету",
		GameStatus.DefeatExtinct => "ПОРАЖЕНИЕ: носителей не осталось",
		_ when engine.Research.Stage == ResearchStage.Vaccine => "Вакцина идёт по миру",
		_ when engine.Research.Setting == ResearchLevel.Off => "Наука молчит — учёные скоро заметят",
		_ => "Мир изучает патоген",
	};

	private static Color StatusColor(GameStatus status) => status switch
	{
		GameStatus.Victory => MapPalette.Success,
		GameStatus.DefeatExtinct => MapPalette.Danger,
		_ => MapPalette.Warning,
	};

	// --- Раскладка ---

	private void Relayout()
	{
		if (_pause is null)
		{
			return;
		}

		float width = Size.X > 1.0f ? Size.X : GetViewportRect().Size.X;

		float right = width - 10.0f;

		var ordered = new List<Button>();
		foreach (Node child in GetChildren())
		{
			if (child is Button button)
			{
				ordered.Add(button);
			}
		}

		for (int i = ordered.Count - 1; i >= 0; i--)
		{
			Button button = ordered[i];
			if (!button.Visible)
			{
				continue;
			}

			right -= button.CustomMinimumSize.X;
			button.Position = new Vector2(right, 9.0f);
			button.Size = new Vector2(button.CustomMinimumSize.X, 26.0f);
			right -= 6.0f;
		}

		float statusWidth = Mathf.Max(0.0f, right - (StatsLeft + 600.0f) - 8.0f);
		bool showStatus = statusWidth >= 150.0f;
		_science.Visible = showStatus;
		_status.Visible = showStatus;
		if (showStatus)
		{
			_science.Size = new Vector2(statusWidth, 16.0f);
			_status.Size = new Vector2(statusWidth, 18.0f);
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized)
		{
			Relayout();
		}
	}

	// --- Помощники ---

	private Label AddStat(float x, float y, float width, string text, int fontSize, Color color)
	{
		var label = new Label
		{
			Text = text,
			Position = new Vector2(x, y),
			CustomMinimumSize = new Vector2(width, 18.0f),
			Size = new Vector2(width, 18.0f),
			MouseFilter = MouseFilterEnum.Ignore,
			ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		};
		label.AddThemeFontSizeOverride("font_size", fontSize);
		label.AddThemeColorOverride("font_color", color);
		AddChild(label);
		return label;
	}

	private Button AddButton(string text, float width, Action action)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(width, 26.0f),
			TooltipText = TooltipFor(text),
		};
		button.AddThemeFontSizeOverride("font_size", 12);
		button.Pressed += action;
		AddChild(button);
		return button;
	}

	private static string TooltipFor(string text) => text switch
	{
		"Пауза" or "Продолжить" => "Пробел",
		"x1" => "Клавиша 1",
		"x3" => "Клавиша 2",
		"x8" => "Клавиша 3",
		"Новая карта" => "Клавиша N",
		_ => string.Empty,
	};

	private static void SetText(Label label, string value)
	{
		if (label.Text != value)
		{
			label.Text = value;
		}
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
		margin.AddThemeConstantOverride("margin_top", 8);
		margin.AddThemeConstantOverride("margin_bottom", 8);
		AddChild(margin);

		var box = new VBoxContainer();
		box.AddThemeConstantOverride("separation", 4);
		margin.AddChild(box);

		_title = new Label { Text = "Регион не выбран" };
		_title.AddThemeFontSizeOverride("font_size", 15);
		_title.AddThemeColorOverride("font_color", MapPalette.Accent);
		box.AddChild(_title);

		_body = new Label
		{
			Text = "Щёлкните по гексу на карте.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_body.AddThemeFontSizeOverride("font_size", 12);
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

		string text = string.Join(
			System.Environment.NewLine,
			$"Население: {WorldGenerator.FormatPopulation(cell.Population)}   " +
			$"медицина: {cell.Healthcare * 100.0:0.#}%",
			$"Город: {cell.Urbanization * 100.0:0.#}%   вода: {cell.WaterAvailability * 100.0:0.#}%   " +
			$"переносчики: {cell.VectorHabitat * 100.0:0.#}%",
			$"Скот: {cell.Livestock * 100.0:0.#}%",
			string.Empty,
			$"Не заражены: {covered:0.#}%   инфекционные: {infectiousShare:0.###}%",
			$"Переболели: {WorldGenerator.FormatPopulation(cell.Recovered)}   " +
			$"умерли: {WorldGenerator.FormatPopulation(cell.Dead)} ({deadShare:0.#}%)",
			$"Вакцинация: {cell.VaccineCoverage * 100.0:0.#}%");

		_title.Text = $"{cell.Coord} — {ClimateData.Name(cell.Climate)}";
		SetBody(text);
	}

	private void SetBody(string text)
	{
		if (_body.Text != text)
		{
			_body.Text = text;
		}
	}
}
