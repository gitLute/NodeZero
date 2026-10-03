using System;
using System.Linq;
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
    private const double BaseSpeed = 2.0;

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

    private double _speed = BaseSpeed;
    private bool _dragging;
    private Vector2 _dragOrigin;
    private int _selectedCell = -1;
    private bool _finished;
    private ulong _seedCounter;

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
        BuildGame(ReadRadius(args));
    }

    // --- Построение игры ---

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

        if (GetNodeOrNull<Node2D>("World") is null)
        {
            BuildScene();
        }

        BindWorld();

        int start = _engine.SuggestPatientZero();
        if (start >= 0)
        {
            _engine.StartOutbreak(start);
            _selectedCell = start;
            _mapView.SetSelected(start);
            _inspector.ShowCell(start);
        }

        _hint.Text =
            $"Сид {_seedCounter}. ЛКМ — выбрать регион, средняя кнопка или пробел + движение — " +
            "перетаскивание карты, колесо — зум.";
    }

    private void BuildScene()
    {
        _config ??= new SimulationConfig();

        _mapView = new HexMapView { Name = "Map" };
        AddChild(_mapView);

        _camera = new Camera2D { Name = "Camera", Enabled = true, PositionSmoothingEnabled = false };
        AddChild(_camera);

        var layer = new CanvasLayer { Name = "Ui" };
        AddChild(layer);

        _topBar = new TopBar { Name = "TopBar" };
        _topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBar.OffsetBottom = 46;
        layer.AddChild(_topBar);
        _topBar.SpeedChanged += OnSpeedChanged;

        // Правая колонка: свойства региона, мутации, хроника.
        var right = new VBoxContainer { Name = "Right" };
        right.AddThemeConstantOverride("separation", 8);
        right.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        right.OffsetLeft = -560;
        right.OffsetTop = 54;
        right.OffsetRight = -8;
        right.OffsetBottom = -8;
        layer.AddChild(right);

        _inspector = new HexInspector
        {
            Name = "Inspector",
            CustomMinimumSize = new Vector2(0, 236),
        };
        right.AddChild(_inspector);

        _mutationPanel = new MutationPanel
        {
            Name = "Mutations",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        right.AddChild(_mutationPanel);

        _feed = new EventFeed
        {
            Name = "Feed",
            CustomMinimumSize = new Vector2(0, 168),
        };
        right.AddChild(_feed);

        _hint = new Label
        {
            Name = "Hint",
            Position = new Vector2(14, 54),
        };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", MapPalette.TextMuted);
        layer.AddChild(_hint);

        _mutationPanel.Notice += text => _hint.Text = text;
    }

    private void BindWorld()
    {
        _mapView.Bind(_engine);
        _topBar.Bind(_engine);
        _inspector.Bind(_engine);
        _mutationPanel.Bind(_engine);
        _feed.Bind(_engine);

        _camera.Position = Vector2.Zero;
        _camera.Zoom = FitZoom();

        _finished = false;
        _topBar.SetSpeed(_speed);
        _topBar.Refresh();
        _mutationPanel.Refresh();
    }

    private Vector2 FitZoom()
    {
        if (_mapView.MapExtent.X <= 0.0f)
        {
            return Vector2.One;
        }

        Vector2 viewport = GetViewportRect().Size;
        float fit = Math.Min(viewport.X, viewport.Y) / (_mapView.MapExtent.X * 2.1f);
        return new Vector2(Mathf.Clamp(fit, 0.25f, 3.0f), Mathf.Clamp(fit, 0.25f, 3.0f));
    }

    // --- Игровой цикл ---

    public override void _Process(double delta)
    {
        if (_engine is null || _mapView is null)
        {
            return;
        }

        if (_speed > 0.0 && !_finished)
        {
            _engine.AdvanceDays(_speed * delta);
            if (_engine.Status != GameStatus.Running)
            {
                _finished = true;
                _hint.Text = _engine.Status == GameStatus.Victory
                    ? "Победа. Нажмите «Новая карта», чтобы начать заново."
                    : "Поражение. Патоген исчез. Нажмите «Новая карта».";
            }
        }

        _topBar.Refresh();
        _mutationPanel.Refresh();
        _feed.Refresh();

        if (_selectedCell >= 0)
        {
            _inspector.ShowCell(_selectedCell);
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
                    _camera.Zoom *= 1.12f;
                    break;

                case MouseButton.WheelDown:
                    _camera.Zoom /= 1.12f;
                    break;

                case MouseButton.Middle:
                    _dragging = button.Pressed;
                    _dragOrigin = button.Position;
                    break;

                case MouseButton.Left when button.Pressed:
                    int cell = _mapView.CellAtPoint(button.Position);
                    if (cell >= 0)
                    {
                        _selectedCell = cell;
                        _mapView.SetSelected(cell);
                        _inspector.ShowCell(cell);
                    }

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
                    _topBar.SetSpeed(_speed > 0.0 ? 0.0 : BaseSpeed);
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

                case Key.F:
                    _camera.Zoom = FitZoom();
                    _camera.Position = Vector2.Zero;
                    break;

                case Key.N:
                    _seedCounter++;
                    BuildGame(null);
                    break;
            }
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