using System;
using System.Collections.Generic;
using Godot;
using NodeZero.App.Ui;
using NodeZero.Core;
using NodeZero.Tests;

namespace NodeZero.App;

/// <summary>
/// Корневая сцена: игровой цикл, камера, ввод и сборка интерфейса.
///
/// <para>Режимы запуска:</para>
/// <list type="bullet">
/// <item>обычный — игра в окне;</item>
/// <item><c>-- --selftest</c> — прогон тестов ядра без окна;</item>
/// <item><c>-- --simulate N</c> — текстовый отчёт по эпидемии;</item>
/// <item><c>-- --trace</c> — пошаговая трассировка ранней эпидемии.</item>
/// </list>
///
/// <para>Темп времени задаётся в сутках в секунду. Каждый кадр сцена продвигает
/// симуляцию на <c>скорость * delta</c> суток; движок сам разбивает это на шаги
/// интегрирования, поэтому точность не зависит от частоты кадров.</para>
/// </summary>
public partial class Main : Node2D
{
    /// <summary>Стадия партии.</summary>
    private enum Phase
    {
        /// <summary>Игрок выбирает регион для нулевого пациента. Время стоит.</summary>
        ChoosingStart,

        /// <summary>Эпидемия идёт.</summary>
        Running,

        /// <summary>Партия закончилась.</summary>
        Finished,
    }

    private SimulationConfig _config = null!;
    private WorldMap _map = null!;
    private Pathogen _pathogen = null!;
    private SimulationEngine _engine = null!;
    private HexMapView _mapView = null!;
    private Camera2D _camera = null!;
    private TopBar _topBar = null!;
    private HexInspector _inspector = null!;
    private MutationPanel _mutationPanel = null!;
    private EventFeed _feed = null!;
    private Label _hint = null!;
    private Label _notice = null!;

    private double _speed = 2.0;
    private bool _dragging;
    private int _selectedCell = -1;
    private Phase _phase = Phase.ChoosingStart;
    private ulong _seedCounter;
    private bool _sceneBuilt;
    private double _uiClock;
    private double _inspectorClock;

    private const double UiRefreshInterval = 0.16;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();

        if (args.Contains("--selftest"))
        {
            int failures = SelfTest.Run(Console.Out);
            GetTree().Quit(failures == 0 ? 0 : 1);
            return;
        }

        if (args.Contains("--simulate") || args.Contains("--trace"))
        {
            RunDiagnostics(args);
            return;
        }

        _seedCounter = ReadSeed(args) ?? (ulong)Time.GetUnixTimeFromSystem();
        BuildScene();
        BuildGame(ReadRadius(args));
    }

    // --- Построение сцены ---

    /// <summary>
    /// Создать дерево узлов. Вызывается ровно один раз: повторный вызов при
    /// генерации новой карты навешивал бы второй слой интерфейса поверх первого,
    /// и верхний слой перехватывал бы ввод, а подписи дублировались бы.
    /// </summary>
    private void BuildScene()
    {
        if (_sceneBuilt)
        {
            return;
        }

        _sceneBuilt = true;

        _mapView = new HexMapView { Name = "Map" };
        AddChild(_mapView);

        _camera = new Camera2D { Name = "Camera", Enabled = true };
        AddChild(_camera);

        var layer = new CanvasLayer { Name = "Ui" };
        AddChild(layer);

        _topBar = new TopBar { Name = "TopBar" };
        _topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBar.OffsetBottom = 46;
        layer.AddChild(_topBar);
        _topBar.SpeedChanged += OnSpeedChanged;
        _topBar.RandomStartRequested += StartRandomOutbreak;

        var right = new VBoxContainer { Name = "Right" };
        right.AddThemeConstantOverride("separation", 8);
        right.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        right.OffsetLeft = -560;
        right.OffsetTop = 54;
        right.OffsetRight = -8;
        right.OffsetBottom = -8;
        layer.AddChild(right);

        _inspector = new HexInspector { Name = "Inspector", CustomMinimumSize = new Vector2(0, 190) };
        right.AddChild(_inspector);

        _mutationPanel = new MutationPanel
        {
            Name = "Mutations",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        right.AddChild(_mutationPanel);

        _feed = new EventFeed { Name = "Feed", CustomMinimumSize = new Vector2(0, 168) };
        right.AddChild(_feed);

        // Подписи живут внизу слева и не пересекаются с правой колонкой.
        _hint = AddHint(layer, new Vector2(0.0f, -40.0f), MapPalette.TextMuted);
        _notice = AddHint(layer, new Vector2(0.0f, -20.0f), MapPalette.Accent);

        _mutationPanel.Notice += SetNotice;
    }

    /// <summary>
    /// Подпись внизу слева, растянутая до левого края правой колонки.
    /// Растягивание по горизонтали обязательно: при нулевом OffsetRight подпись
    /// получает нулевую ширину и просто не рисуется.
    /// </summary>
    private static Label AddHint(CanvasLayer layer, Vector2 offset, Color color)
    {
        var label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", color);

        label.AnchorLeft = 0.0f;
        label.AnchorRight = 1.0f;
        label.AnchorTop = 1.0f;
        label.AnchorBottom = 1.0f;
        label.OffsetLeft = 14.0f;
        label.OffsetRight = -576.0f;
        label.OffsetTop = offset.Y;
        label.OffsetBottom = offset.Y + 18.0f;

        layer.AddChild(label);
        return label;
    }

    // --- Построение партии ---

    private void BuildGame(int? radius)
    {
        var defaults = new SimulationConfig();
        _config = new SimulationConfig
        {
            Seed = _seedCounter,
            MapRadius = radius ?? defaults.MapRadius,
        };

        _map = WorldGenerator.Generate(_config);
        _pathogen = new Pathogen(_config);
        _engine = new SimulationEngine(_map, _pathogen, _config);

        _mapView.Bind(_engine);
        _topBar.Bind(_engine);
        _inspector.Bind(_engine);
        _mutationPanel.Bind(_engine);
        _feed.Bind(_engine);

        _camera.Position = Vector2.Zero;
        _camera.Zoom = FitZoom();

        _selectedCell = -1;
        _mapView.SetSelected(-1);
        _inspector.ShowCell(-1);
        _phase = Phase.ChoosingStart;
        _topBar.SetStartButtonVisible(true);

        SetNotice(string.Empty);
        SetHint(
            $"Сид {_seedCounter}. Выберите регион для нулевого пациента — левый клик по гексу " +
            "или кнопка «Случайный старт». Время стоит, пока вы выбираете.");
    }

    private Vector2 FitZoom()
    {
        if (_mapView.MapExtent.X <= 0.0f)
        {
            return Vector2.One;
        }

        Vector2 viewport = GetViewportRect().Size;
        float fit = Math.Min(viewport.X, viewport.Y) / (_mapView.MapExtent.X * 2.1f);
        float zoom = Mathf.Clamp(fit, 0.25f, 3.0f);
        return new Vector2(zoom, zoom);
    }

    // --- Игровой цикл ---

    public override void _Process(double delta)
    {
        if (_engine is null || _mapView is null)
        {
            return;
        }

        if (_phase == Phase.Running && _speed > 0.0)
        {
            _engine.AdvanceDays(_speed * delta);

            if (_engine.Status != GameStatus.Running)
            {
                _phase = Phase.Finished;
                _topBar.SetStartButtonVisible(false);
                SetHint(_engine.Status == GameStatus.Victory
                    ? "Победа. N — новая карта."
                    : "Поражение: носителей не осталось. N — новая карта.");
            }
        }

        // Интерфейс обновляется не чаще шести раз в секунду: иначе числа в полосе
        // мельтешат, а строки инспектора пересобираются на каждом кадре впустую.
        _uiClock += delta;
        if (_uiClock < UiRefreshInterval)
        {
            return;
        }

        double now = Time.GetTicksMsec() / 1000.0;
        _uiClock = 0.0;

        _topBar.Refresh(nowSeconds: now);
        _mutationPanel.Refresh();
        _feed.Refresh();

        _inspectorClock += UiRefreshInterval;
        if (_inspectorClock >= UiRefreshInterval)
        {
            _inspectorClock = 0.0;
            if (_selectedCell >= 0)
            {
                _inspector.ShowCell(_selectedCell);
            }
        }
    }

    private void OnSpeedChanged(double value)
    {
        if (value < 0.0)
        {
            _seedCounter++;
            BuildGame(null);
            return;
        }

        _speed = value;
    }

    private void StartRandomOutbreak()
    {
        if (_phase != Phase.ChoosingStart)
        {
            return;
        }

        int start = _engine.SuggestPatientZero();
        if (start >= 0)
        {
            BeginOutbreak(start);
        }
    }

    private void BeginOutbreak(int cellIndex)
    {
        _engine.StartOutbreak(cellIndex);

        _selectedCell = cellIndex;
        _mapView.SetSelected(cellIndex);
        _inspector.ShowCell(cellIndex);

        _phase = Phase.Running;
        _topBar.SetStartButtonVisible(false);
        _topBar.SetSpeed(_speed > 0.0 ? _speed : 2.0);
        _topBar.Refresh(force: true);

        HexCell cell = _engine.Map.Cells[cellIndex];
        SetHint(
            $"Сид {_seedCounter}. ЛКМ — выбрать регион, средняя кнопка — тянуть карту, " +
            $"колесо или плюс и минус — зум, пробел — пауза, F — вписать карту, N — новая карта. " +
            $"Старт: {cell.Coord}, {ClimateData.Name(cell.Climate)}, " +
            $"контактный вес {_engine.RelativeContactWeight(cellIndex):0.000}.");
    }

    // --- Ввод ---

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_engine is null || _mapView is null)
        {
            return;
        }

        if (@event is InputEventMouseButton button)
        {
            switch (button.ButtonIndex)
            {
                case MouseButton.WheelUp:
                    // Если курсор над прокручиваемой панелью, колесо принадлежит ей.
                    if (IsPointerOverScrollable())
                    {
                        return;
                    }

                    _camera.Zoom *= 1.12f;
                    break;

                case MouseButton.WheelDown:
                    if (IsPointerOverScrollable())
                    {
                        return;
                    }

                    _camera.Zoom /= 1.12f;
                    break;

                case MouseButton.Middle:
                    _dragging = button.Pressed;
                    break;

                case MouseButton.Left when button.Pressed:
                    int cell = _mapView.CellAtPoint(button.Position);
                    if (cell < 0)
                    {
                        break;
                    }

                    if (_phase == Phase.ChoosingStart)
                    {
                        BeginOutbreak(cell);
                        break;
                    }

                    _selectedCell = cell;
                    _mapView.SetSelected(cell);
                    _inspector.ShowCell(cell);
                    break;
            }

            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            _mapView.SetHover(_mapView.CellAtPoint(motion.Position));

            if (_dragging)
            {
                _camera.Position -= motion.Relative / _camera.Zoom.X;
            }

            return;
        }

        if (@event is InputEventKey key && key.Pressed)
        {
            switch (key.Keycode)
            {
                case Key.Space:
                    _topBar.SetSpeed(_speed > 0.0 ? 0.0 : 2.0);
                    break;

                case Key.Key1:
                    _topBar.SetSpeed(1.0);
                    break;

                case Key.Key2:
                    _topBar.SetSpeed(3.0);
                    break;

                case Key.Key3:
                    _topBar.SetSpeed(8.0);
                    break;

                case Key.Equal:
                case Key.KpAdd:
                    _camera.Zoom *= 1.12f;
                    break;

                case Key.Minus:
                case Key.KpSubtract:
                    _camera.Zoom /= 1.12f;
                    break;

                case Key.F:
                    _camera.Zoom = FitZoom();
                    _camera.Position = Vector2.Zero;
                    break;

                case Key.Enter:
                case Key.KpEnter:
                    StartRandomOutbreak();
                    break;

                case Key.N:
                    _seedCounter++;
                    BuildGame(null);
                    break;
            }
        }
    }

    /// <summary>
    /// Прокручивается ли под курсором панель. Требуется, чтобы колесо мыши над
    /// списком мутаций или хроникой крутило список, а не приближало карту.
    /// </summary>
    private bool IsPointerOverScrollable()
    {
        Control? control = GetViewport().GuiGetHoveredControl();

        while (control is not null)
        {
            if (control is ScrollContainer)
            {
                return true;
            }

            control = control.GetParent() as Control;
        }

        return false;
    }

    private void SetHint(string text)
    {
        if (_hint.Text != text)
        {
            _hint.Text = text;
        }
    }

    private void SetNotice(string text)
    {
        if (_notice.Text != text)
        {
            _notice.Text = text;
        }
    }

    // --- Диагностические режимы ---

    private void RunDiagnostics(string[] args)
    {
        var config = new SimulationConfig
        {
            Seed = ReadSeed(args) ?? 20261003UL,
            MapRadius = ReadRadius(args) ?? 12,
        };

        int days = ReadDays(args) ?? 720;
        bool trace = args.Contains("--trace");

        var options = new SimulationReportOptions
        {
            Days = days,
            SampleDays = Math.Max(10, days / 24),
            Trace = trace,
            TraceDays = ReadTraceDays(args) ?? 240,
            Script = Strategies.ByName(ReadValue(args, "--strategy")),
        };

        SimulationReport.Run(Console.Out, config, options);
        GetTree().Quit(0);
    }

    private static int? ReadDays(string[] args) => ReadInt(args, "--simulate");

    private static int? ReadRadius(string[] args) => ReadInt(args, "--radius");

    private static int? ReadTraceDays(string[] args) => ReadInt(args, "--trace-days");

    private static ulong? ReadSeed(string[] args)
    {
        string? raw = ReadValue(args, "--seed");
        return raw is not null && ulong.TryParse(raw, out ulong value) ? value : null;
    }

    private static int? ReadInt(string[] args, string key)
    {
        string? raw = ReadValue(args, key);
        return raw is not null && int.TryParse(raw, out int value) ? value : null;
    }

    private static string? ReadValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}