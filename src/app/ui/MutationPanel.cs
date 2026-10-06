using System;
using System.Collections.Generic;
using Godot;
using NodeZero.Core;

namespace NodeZero.App.Ui;

/// <summary>
/// Панель мутаций. Каждая строка показывает название, текущий уровень, эффект
/// и цену следующего уровня; кнопка покупки неактивна, если не хватает очков ДНК
/// или не выполнены требования. Рядом — сводка по патогену.
/// </summary>
public partial class MutationPanel : PanelContainer
{
    private sealed record Row(
        MutationId Id,
        Label Name,
        Label Levels,
        Label Effect,
        Button Buy,
        Label Price);

    private readonly List<Row> _rows = new();
    private Label _summary = null!;
    private VBoxContainer _list = null!;
    private SimulationEngine? _engine;
    private string? _notice;

    public event System.Action<string>? Notice;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", TopBar.PanelStyle());

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        var title = new Label { Text = "Мутации патогена" };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", MapPalette.Accent);
        column.AddChild(title);

        _summary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _summary.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_summary);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);

        _list = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        var researchTitle = new Label { Text = "Исследования" };
        researchTitle.AddThemeFontSizeOverride("font_size", 14);
        researchTitle.AddThemeColorOverride("font_color", MapPalette.TextMuted);
        column.AddChild(researchTitle);

        var researchRow = new HBoxContainer();
        column.AddChild(researchRow);

        foreach ((string label, ResearchLevel level) in new[]
                 {
                     ("Выключить", ResearchLevel.Off),
                     ("Вполсилы", ResearchLevel.Reduced),
                     ("Полные силы", ResearchLevel.Normal),
                 })
        {
            ResearchLevel value = level;
            var button = new Button { Text = label, ToggleMode = true };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += () => SetResearch(value);
            researchRow.AddChild(button);
        }
    }

    private void SetResearch(ResearchLevel level)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Research.Setting = level;
        Notice?.Invoke($"Режим исследований: {Describe(level)}.");
        Refresh();
    }

    private static string Describe(ResearchLevel level) => level switch
    {
        ResearchLevel.Off => "выключены",
        ResearchLevel.Reduced => "вполсилы",
        _ => "полные силы",
    };

    public void Bind(SimulationEngine engine)
    {
        _engine = engine;
        BuildRows();
        Refresh();
    }

    private void BuildRows()
    {
        if (_engine is null || _rows.Count > 0)
        {
            return;
        }

        foreach (string category in new[]
                 {
                     MutationCatalog.CategoryTransmission,
                     MutationCatalog.CategoryProperties,
                     MutationCatalog.CategoryEnvironment,
                     MutationCatalog.CategoryClinical,
                 })
        {
            var header = new Label { Text = category };
            header.AddThemeFontSizeOverride("font_size", 13);
            header.AddThemeColorOverride("font_color", MapPalette.Warning);
            _list.AddChild(header);

            foreach (MutationDef def in MutationCatalog.All)
            {
                if (def.Category != category)
                {
                    continue;
                }

                var name = new Label
                {
                    Text = def.Name,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                name.AddThemeFontSizeOverride("font_size", 14);

                var levels = new Label { CustomMinimumSize = new Vector2(52, 0) };
                levels.AddThemeFontSizeOverride("font_size", 13);
                levels.AddThemeColorOverride("font_color", MapPalette.TextMuted);

                var buy = new Button { Text = "+", CustomMinimumSize = new Vector2(30, 0) };
                MutationId id = def.Id;
                buy.Pressed += () => Buy(id);

                var price = new Label { CustomMinimumSize = new Vector2(34, 0) };
                price.AddThemeFontSizeOverride("font_size", 12);
                price.AddThemeColorOverride("font_color", MapPalette.TextMuted);

                var header1 = new HBoxContainer();
                header1.AddChild(name);
                header1.AddChild(levels);
                header1.AddChild(buy);
                header1.AddChild(price);
                _list.AddChild(header1);

                var effect = new Label
                {
                    Text = def.Effect,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                effect.AddThemeFontSizeOverride("font_size", 11);
                effect.AddThemeColorOverride("font_color", MapPalette.TextMuted);
                effect.CustomMinimumSize = new Vector2(0, 26);
                _list.AddChild(effect);

                _rows.Add(new Row(def.Id, name, levels, effect, buy, price));
            }
        }
    }

    private void Buy(MutationId id)
    {
        if (_engine is null)
        {
            return;
        }

        if (_engine.TryMutate(id, out string? refusal))
        {
            MutationDef def = MutationCatalog.Get(id);
            _notice = $"Мутация «{def.Name}»: уровень {_engine.Pathogen.Level(id)}.";
        }
        else
        {
            _notice = refusal ?? "Мутация недоступна.";
        }

        Notice?.Invoke(_notice);
        Refresh();
    }

    public void Refresh()
    {
        if (_engine is null || _rows.Count == 0)
        {
            return;
        }

        Pathogen pathogen = _engine.Pathogen;

        _summary.Text = string.Join(
            System.Environment.NewLine,
            $"R0 = {_engine.Pathogen.BasicReproduction:0.00}   " +
            $"бета = {_engine.Pathogen.BasicReproduction / Math.Max(0.01, _engine.Pathogen.InfectiousDays):0.000}/сут   " +
            $"R_t = {_engine.InstantaneousReproduction:0.00}",
            $"Инкубационный период {_engine.Pathogen.IncubationDays:0.0} сут   " +
            $"заразный период {_engine.Pathogen.InfectiousDays:0.0} сут   " +
            $"иммунитет {_engine.Pathogen.ImmunityDays:0} сут " +
            $"({_engine.Pathogen.ImmunityToInfectiousRatio:0.0} заразных периодов)",
            $"Симптомы есть у {_engine.Pathogen.SymptomaticFraction * 100.0:0.#}% заражённых   " +
            $"смертность {_engine.Pathogen.SeverityFactor * 100.0:0.0}%   " +
            $"устойчивость к медицине {_engine.Pathogen.MedicineResistanceFactor * 100.0:0.#}%",
            $"ДНК: доступно {pathogen.AvailableDna:0.#} из возможных {pathogen.DnaCap:0.#}   " +
            $"переболевших {_engine.InfectedEverShare * 100.0:0.#}% населения");

        foreach (Row row in _rows)
        {
            MutationDef def = MutationCatalog.Get(row.Id);
            int level = pathogen.Level(row.Id);

            row.Levels.Text = def.MaxLevel > 1
                ? $"{level}/{def.MaxLevel}"
                : level > 0 ? "открыт" : "закрыт";

            int cost = pathogen.CostToNextLevel(row.Id);
            bool affordable = pathogen.CanMutate(row.Id);
            bool maxed = pathogen.IsAtMax(row.Id);

            row.Buy.Disabled = !affordable;
            row.Buy.Text = maxed ? "—" : "+";
            row.Price.Text = maxed ? string.Empty : $"{cost}";

            Color color = maxed
                ? MapPalette.TextMuted
                : affordable ? MapPalette.Success : MapPalette.TextMuted;
            row.Price.AddThemeColorOverride("font_color", color);

            if (maxed)
            {
                row.Effect.Text = def.Effect;
            }
            else if (!pathogen.PrerequisitesMet(row.Id))
            {
                row.Effect.Text = $"Требуется: {pathogen.DescribeRequirements(row.Id)}.";
                row.Effect.AddThemeColorOverride("font_color", MapPalette.Warning);
            }
            else if (!affordable)
            {
                row.Effect.Text = def.Effect;
                row.Effect.AddThemeColorOverride("font_color", MapPalette.TextMuted);
            }
            else
            {
                row.Effect.Text = def.Effect;
                row.Effect.AddThemeColorOverride("font_color", MapPalette.TextMuted);
            }
        }
    }
}

/// <summary>Новостная лента: вспышки, мутации, научные события, итог партии.</summary>
public partial class EventFeed : PanelContainer
{
    private VBoxContainer _list = null!;
    private readonly List<Row> _rows = new();
    private SimulationEngine? _engine;

    private sealed record Row(Label Text, GameEventKind Kind);

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", TopBar.PanelStyle());

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        margin.AddChild(column);

        var title = new Label { Text = "Хроника мира" };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", MapPalette.Accent);
        column.AddChild(title);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_list);
    }

    public void Bind(SimulationEngine engine)
    {
        _engine = engine;
        foreach (Row row in _rows)
        {
            row.Text.QueueFree();
        }

        _rows.Clear();
        Refresh();
    }

    public void Refresh()
    {
        if (_engine is null)
        {
            return;
        }

        int shown = 0;
        var engine = _engine;

        for (int i = engine.Events.Count - 1; i >= 0 && shown < 40; i--)
        {
            GameEvent entry = engine.Events[i];
            if (_rows.Count <= shown)
            {
                var label = new Label
                {
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                label.AddThemeFontSizeOverride("font_size", 12);
                _list.AddChild(label);
                _rows.Add(new Row(label, entry.Kind));
            }

            Row row = _rows[shown];
            string text = $"день {entry.Day,5}  {entry.Text}";
            if (row.Text.Text != text)
            {
                row.Text.Text = text;
                row.Text.AddThemeColorOverride("font_color", KindColor(entry.Kind));
            }

            shown++;
        }

        for (int i = shown; i < _rows.Count; i++)
        {
            _rows[i].Text.Text = string.Empty;
        }
    }

    private static Color KindColor(GameEventKind kind) => kind switch
    {
        GameEventKind.Victory => MapPalette.Success,
        GameEventKind.Defeat => MapPalette.Danger,
        GameEventKind.Cure or GameEventKind.Vaccine or GameEventKind.Identification => MapPalette.Warning,
        GameEventKind.Mutation => MapPalette.Accent,
        _ => MapPalette.TextMuted,
    };
}
