using System;
using Godot;
using NodeZero.Core;

namespace NodeZero.App;

/// <summary>
/// Цветовая схема карты. Значения заданы явно, чтобы прототип не зависел от
/// ресурсов-текстур: все цвета вычисляются в коде.
/// </summary>
public static class MapPalette
{
    public static readonly Color Ocean = new(0.043f, 0.075f, 0.129f);
    public static readonly Color OceanDeep = new(0.027f, 0.047f, 0.086f);
    public static readonly Color Coast = new(0.098f, 0.161f, 0.235f);

    // Материк по климату.
    public static readonly Color Polar = new(0.729f, 0.780f, 0.831f);
    public static readonly Color ColdDry = new(0.404f, 0.475f, 0.361f);
    public static readonly Color ColdHumid = new(0.212f, 0.361f, 0.243f);
    public static readonly Color HotDry = new(0.741f, 0.624f, 0.365f);
    public static readonly Color HotHumid = new(0.192f, 0.427f, 0.216f);

    public static readonly Color Infected = new(0.878f, 0.216f, 0.216f);
    public static readonly Color Dead = new(0.106f, 0.086f, 0.094f);
    public static readonly Color Vaccine = new(0.310f, 0.639f, 0.925f);

    public static readonly Color Border = new(0.0f, 0.0f, 0.0f, 0.22f);
    public static readonly Color BorderSelected = new(1.0f, 0.898f, 0.435f);
    public static readonly Color BorderPatientZero = new(1.0f, 0.98f, 0.98f);
    public static readonly Color BorderHover = new(1.0f, 1.0f, 1.0f, 0.45f);

    public static readonly Color PanelBackground = new(0.055f, 0.071f, 0.094f, 0.94f);
    public static readonly Color PanelBorder = new(0.169f, 0.212f, 0.278f);
    public static readonly Color TextPrimary = new(0.898f, 0.925f, 0.957f);
    public static readonly Color TextMuted = new(0.549f, 0.604f, 0.671f);
    public static readonly Color Accent = new(0.310f, 0.639f, 0.925f);
    public static readonly Color Warning = new(0.925f, 0.639f, 0.310f);
    public static readonly Color Danger = new(0.878f, 0.216f, 0.216f);
    public static readonly Color Success = new(0.310f, 0.792f, 0.475f);

    public static Color Land(Climate climate) => climate switch
    {
        Climate.Polar => Polar,
        Climate.ColdDry => ColdDry,
        Climate.ColdHumid => ColdHumid,
        Climate.HotDry => HotDry,
        Climate.HotHumid => HotHumid,
        _ => ColdHumid,
    };

    /// <summary>Океан с лёгкой зависимостью от глубины (расстояния от берега).</summary>
    public static Color Water(double distanceFromCenter)
    {
        float t = (float)Math.Clamp(distanceFromCenter, 0.0, 1.0);
        return OceanDeep.Lerp(Ocean, t);
    }

    /// <summary>
    /// Цвет региона: материк по климату, поверх которого накладываются заражение,
    /// смертность и вакцинация.
    /// </summary>
    public static Color Region(double susceptible, double infectious, double dead, double vaccine, Climate climate, out double severity)
    {
        Color baseColor = Land(climate);
        double living = susceptible + infectious;
        double scale = living + dead;

        severity = scale > 0.0 ? infectious / scale : 0.0;

        // Основной признак — доля населения под наркозом болезни.
        Color result = baseColor.Lerp(Infected, (float)Math.Clamp(severity * 1.35, 0.0, 1.0));

        if (dead > 0.0 && scale > 0.0)
        {
            double deadShare = Math.Clamp(dead / scale * 0.85, 0.0, 0.8);
            result = result.Lerp(Dead, (float)deadShare);
        }

        if (vaccine > 0.02)
        {
            result = result.Lerp(Vaccine, (float)Math.Clamp(vaccine * 0.45, 0.0, 0.45));
        }

        return result;
    }

    /// <summary>Легенда климатов для панели интерфейса.</summary>
    public static (string Name, Color Color)[] ClimateLegend()
    {
        var legend = new (string, Color)[ClimateData.Count];
        for (int i = 0; i < ClimateData.Count; i++)
        {
            var climate = (Climate)i;
            legend[i] = (ClimateData.Name(climate), Land(climate));
        }

        return legend;
    }
}