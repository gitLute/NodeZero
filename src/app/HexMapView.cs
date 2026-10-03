using System;
using Godot;
using NodeZero.Core;

namespace NodeZero.App;

/// <summary>
/// Отрисовка карты. Никаких текстур: все гексы рисуются кодом через <c>_Draw</c>,
/// поэтому прототип запускается на пустом проекте без единого ассета.
///
/// Карта перестраивается только при изменении состояния симуляции или выделения,
/// а не каждый кадр: для прототипа на 300-500 гексов этого достаточно.
/// </summary>
public partial class HexMapView : Node2D
{
    private SimulationEngine? _engine;
    private HexLayout? _layout;
    private Vector2[][] _polygons = Array.Empty<Vector2[]>();
    private Vector2[] _centers = Array.Empty<Vector2>();
    private int _hoverCell = -1;
    private int _selectedCell = -1;
    private bool _dirty = true;
    private ulong _lastHash;

    /// <summary>Число гексов, нарисованных в последнем кадре.</summary>
    public int DrawnCells { get; private set; }

    /// <summary>Смещение центра карты в пикселях — используется для начального зума.</summary>
    public Vector2 MapExtent { get; private set; }

    /// <summary>Привязать движок. При смене мира геометрия перестраивается.</summary>
    public void Bind(SimulationEngine engine)
    {
        _engine = engine;
        _lastHash = 0;
        BuildGeometry();
        _dirty = true;
    }

    public void SetSelected(int cellIndex)
    {
        if (_selectedCell == cellIndex)
        {
            return;
        }

        _selectedCell = cellIndex;
        _dirty = true;
    }

    public void SetHover(int cellIndex)
    {
        if (_hoverCell == cellIndex)
        {
            return;
        }

        _hoverCell = cellIndex;
        _dirty = true;
    }

    /// <summary>
    /// Найти гекс под точкой экрана. Пиксели переводятся в систему координат карты
    /// с учётом текущего зума камеры.
    /// </summary>
    public int CellAtPoint(Vector2 screenPosition)
    {
        if (_layout is null || _engine is null)
        {
            return -1;
        }

        Vector2 world = GetGlobalTransformWithCanvas().AffineInverse() * screenPosition;

        double bestDistance = double.MaxValue;
        int best = -1;

        foreach (int index in _engine.Map.LandIndices)
        {
            double dx = world.X - _centers[index].X;
            double dy = world.Y - _centers[index].Y;
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        double reach = _layout.HexSize * _layout.HexSize * 1.3;
        return bestDistance <= reach ? best : -1;
    }

    public override void _Process(double delta)
    {
        if (_engine is null)
        {
            return;
        }

        ulong hash = _engine.StateHash();
        if (hash != _lastHash)
        {
            _lastHash = hash;
            _dirty = true;
        }

        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_engine is null || _layout is null)
        {
            return;
        }

        WorldMap map = _engine.Map;

        // Фон-океан под всем контуром карты.
        DrawRect(new Rect2(-MapExtent, MapExtent * 2.0f), MapPalette.OceanDeep, true);

        DrawnCells = 0;

        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            Color fill = cell.IsLand
                ? MapPalette.Region(cell.Susceptible, cell.Infectious, cell.Dead,
                    cell.VaccineCoverage, cell.Climate, out _)
                : MapPalette.Coast;

            DrawColoredPolygon(_polygons[index], fill);
            DrawnCells++;
        }

        // Границы вторым проходом, иначе линии тонут в заливке соседей.
        foreach (int index in map.LandIndices)
        {
            DrawPolyline(_polygons[index], MapPalette.Border, 1.0f, true);
        }

        foreach (int index in map.LandIndices)
        {
            HexCell cell = map.Cells[index];
            if (!cell.IsLand)
            {
                continue;
            }

            if (index == _selectedCell)
            {
                DrawPolyline(_polygons[index], MapPalette.BorderSelected, 3.0f, true);
            }
            else if (index == _hoverCell)
            {
                DrawPolyline(_polygons[index], MapPalette.BorderHover, 2.0f, true);
            }

            if (cell.OutbreakReported && cell.Infectious > 0.0)
            {
                DrawCircle(_centers[index], 3.5f, MapPalette.Infected);
            }
        }
    }

    private void BuildGeometry()
    {
        if (_engine is null)
        {
            return;
        }

        _layout = new HexLayout(30f);
        HexCell[] cells = _engine.Map.Cells;

        _polygons = new Vector2[cells.Length][];
        _centers = new Vector2[cells.Length];

        float extent = 0.0f;

        for (int index = 0; index < cells.Length; index++)
        {
            Point2 center = _layout.Center(cells[index].Coord);
            _centers[index] = new Vector2(center.X, center.Y);
            extent = Math.Max(extent, Math.Abs(center.X) + _layout.HexSize);
            extent = Math.Max(extent, Math.Abs(center.Y) + _layout.HexSize);

            Point2[] corners = _layout.Corners(center);
            // Замыкаем контур повторной первой вершиной: этого требует DrawPolyline.
            var closed = new Vector2[corners.Length + 1];
            for (int i = 0; i < corners.Length; i++)
            {
                closed[i] = new Vector2(corners[i].X, corners[i].Y);
            }

            closed[^1] = closed[0];
            _polygons[index] = closed;
        }

        MapExtent = new Vector2(extent, extent);
        _dirty = true;
    }
}