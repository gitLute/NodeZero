using System;

namespace NodeZero.Core;

/// <summary>Точка на плоскости без зависимости от Godot.</summary>
public readonly struct Point2(float x, float y)
{
    public readonly float X = x;
    public readonly float Y = y;

    public override string ToString() => $"({X:0.##}, {Y:0.##})";
}

/// <summary>
/// Геометрия pointy-top гексов: перевод аксиальных координат в пиксели и построение
/// контура шестиугольника. Держится вне слоя Godot, чтобы ядро симуляции оставалось
/// тестируемым без движка.
/// </summary>
public sealed class HexLayout
{
    private const float SqrtThree = 1.7320508f;

    /// <summary>Расстояние от центра гекса до его вершины.</summary>
    public float HexSize { get; }

    /// <summary>Ширина гекса (расстояние между центрами соседей по горизонтали).</summary>
    public float Width => SqrtThree * HexSize;

    /// <summary>Высота гекса от верхней до нижней вершины.</summary>
    public float Height => 2f * HexSize;

    /// <summary>Вертикальный шаг между рядами гексов.</summary>
    public float VerticalStep => 1.5f * HexSize;

    public HexLayout(float hexSize = 28f)
    {
        HexSize = hexSize;
    }

    /// <summary>Центр гекса в пикселях относительно центра карты.</summary>
    public Point2 Center(HexCoord coord) => new(
        HexSize * SqrtThree * (coord.Q + coord.R / 2f),
        HexSize * 1.5f * coord.R);

    /// <summary>Шесть вершин гекса против часовой стрелки, начиная с верхней.</summary>
    public Point2[] Corners(Point2 center)
    {
        var corners = new Point2[6];
        for (int i = 0; i < 6; i++)
        {
            double angle = Math.PI / 180.0 * (60.0 * i - 30.0);
            corners[i] = new Point2(
                center.X + HexSize * (float)Math.Cos(angle),
                center.Y + HexSize * (float)Math.Sin(angle));
        }

        return corners;
    }

    /// <summary>Обратное преобразование пикселей в аксиальные координаты (с кубическим округлением).</summary>
    public HexCoord ToCoord(Point2 pixel)
    {
        float q = (SqrtThree / 3f * pixel.X - 1f / 3f * pixel.Y) / HexSize;
        float r = (2f / 3f * pixel.Y) / HexSize;

        return RoundToNearestPixel(q, r);
    }

    /// <summary>
    /// Округление дробных аксиальных координат до ближайшего центра гекса
    /// через кубические координаты: отбрасывается тот дробный компонент,
    /// который дальше от целого.
    /// </summary>
    private static HexCoord RoundToNearestPixel(double q, double r)
    {
        double cubeX = q;
        double cubeZ = r;
        double cubeY = -cubeX - cubeZ;

        double rx = Math.Round(cubeX);
        double ry = Math.Round(cubeY);
        double rz = Math.Round(cubeZ);

        double dx = Math.Abs(rx - cubeX);
        double dy = Math.Abs(ry - cubeY);
        double dz = Math.Abs(rz - cubeZ);

        if (dx > dy && dx > dz)
        {
            rx = -ry - rz;
        }
        else if (dy > dz)
        {
            ry = -rx - rz;
        }
        else
        {
            rz = -rx - ry;
        }

        return new HexCoord((int)rx, (int)rz);
    }
}