using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 自动化设置控件。全部用代码搭，不写 axaml。
/// </summary>
/// <remarks>
/// 插件只引用了 FluentAvalonia（没有 Avalonia.Themes.Fluent），而本仓库的设置页是
/// 用 <c>AvaloniaXamlLoader.Load</c> 运行时加载 axaml 的。给这三个小面板再加 axaml
/// 会引入一套独立的资源解析路径，反而容易在插件的 PluginLoadContext 里找不到模板。
/// 用纯代码建控件最稳：TextBox / ComboBox / CheckBox 都走宿主已有的主题。
/// <para/>
/// 设置对象由宿主在控件挂到可视树<b>之前</b>注入，所以绑定放在
/// <see cref="Visual.OnAttachedToVisualTree"/> 里做，构造函数里读 Settings 会是 null。
/// </remarks>
internal static class PickerControlHelper
{
    internal static StackPanel Row(string label, Control input) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Margin = new Thickness(0, 2),
        Children =
        {
            new TextBlock
            {
                Text = label,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center
            },
            input
        }
    };

    internal static StackPanel Panel(params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(4) };
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        return panel;
    }

    internal static TextBox TextBox(string watermark, int minWidth = 200) => new()
    {
        Watermark = watermark,
        MinWidth = minWidth
    };

    internal static ComboBox Combo(string[] items, int minWidth = 160) => new()
    {
        ItemsSource = items,
        MinWidth = minWidth,
        SelectedIndex = 0
    };
}

/// <summary>「当抽中指定名字时」触发器的设置。</summary>
public sealed class PickerTriggerSettingsControl : TriggerSettingsControlBase<PickerNameMatchSettings>
{
    private readonly TextBox _name = PickerControlHelper.TextBox("例如：张三,李四（也可用顿号或换行）", 240);
    private readonly ComboBox _mode = PickerControlHelper.Combo(["完全相等", "包含这段文字"]);
    private bool _bound;

    public PickerTriggerSettingsControl()
    {
        Content = PickerControlHelper.Panel(
            PickerControlHelper.Row("抽中这些人", _name),
            PickerControlHelper.Row("匹配方式", _mode));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_bound)
        {
            return;
        }

        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        _bound = true;
        _name.Text = settings.Name ?? string.Empty;
        _mode.SelectedIndex = settings.MatchMode == "contains" ? 1 : 0;
        _name.TextChanged += (_, _) => settings.Name = _name.Text ?? string.Empty;
        _mode.SelectionChanged += (_, _) =>
            settings.MatchMode = _mode.SelectedIndex == 1 ? "contains" : "exact";
    }
}

/// <summary>四个抽选类行动共用的设置：人数、名单、显示开关。</summary>
public sealed class PickerActionSettingsControl : ActionSettingsControlBase<PickerActionSettings>
{
    private readonly TextBox _count = PickerControlHelper.TextBox("1", 90);
    private readonly ComboBox _roster = PickerControlHelper.Combo(["跟随设置", "主名单", "文字名单"]);
    private readonly ComboBox _reveal = PickerControlHelper.Combo(["显示中央大字", "不显示中央大字"]);
    private readonly ComboBox _notify = PickerControlHelper.Combo(["发送提醒", "不发送提醒"]);
    private bool _bound;

    public PickerActionSettingsControl()
    {
        Content = PickerControlHelper.Panel(
            PickerControlHelper.Row("抽取人数", _count),
            PickerControlHelper.Row("用哪份名单", _roster),
            PickerControlHelper.Row("抽中之后", _reveal),
            PickerControlHelper.Row("主界面提醒", _notify));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_bound)
        {
            return;
        }

        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        _bound = true;
        _count.Text = Math.Max(1, settings.Count).ToString();
        _roster.SelectedIndex = settings.Roster switch
        {
            "main" => 1,
            "text" => 2,
            _ => 0
        };
        _reveal.SelectedIndex = settings.ShowCenterReveal ? 0 : 1;
        _notify.SelectedIndex = settings.ShowNotification ? 0 : 1;

        _count.TextChanged += (_, _) =>
            settings.Count = int.TryParse(_count.Text, out var n) && n > 0 ? n : 1;
        _roster.SelectionChanged += (_, _) => settings.Roster = _roster.SelectedIndex switch
        {
            1 => "main",
            2 => "text",
            _ => "default"
        };
        _reveal.SelectionChanged += (_, _) => settings.ShowCenterReveal = _reveal.SelectedIndex != 1;
        _notify.SelectionChanged += (_, _) => settings.ShowNotification = _notify.SelectedIndex != 1;
    }
}

/// <summary>规则设置：名字、抽选方式、本轮剩余人数上限。</summary>
public sealed class PickerRuleSettingsControl : RuleSettingsControlBase<PickerRuleSettings>
{
    private readonly TextBox _name = PickerControlHelper.TextBox("例如：张三（多个用逗号分隔）", 220);
    private readonly ComboBox _mode = PickerControlHelper.Combo(["完全相等", "包含这段文字"]);
    private readonly ComboBox _source = PickerControlHelper.Combo(
        ["任意", "名单抽人", "文字名单", "拍照抽人", "一次抽多人", "只抽一人"]);
    private readonly TextBox _remaining = PickerControlHelper.TextBox("0 = 不限制", 120);
    private bool _bound;

    public PickerRuleSettingsControl()
    {
        Content = PickerControlHelper.Panel(
            PickerControlHelper.Row("名字", _name),
            PickerControlHelper.Row("名字匹配", _mode),
            PickerControlHelper.Row("抽选方式", _source),
            PickerControlHelper.Row("剩余人数上限", _remaining));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_bound)
        {
            return;
        }

        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        _bound = true;
        _name.Text = settings.Name ?? string.Empty;
        _mode.SelectedIndex = settings.MatchMode == "contains" ? 1 : 0;
        _source.SelectedIndex = settings.Source switch
        {
            "roster" => 1,
            "text" => 2,
            "photo" => 3,
            "multi" => 4,
            "single" => 5,
            _ => 0
        };
        _remaining.Text = Math.Max(0, settings.MaxRemaining).ToString();

        _name.TextChanged += (_, _) => settings.Name = _name.Text ?? string.Empty;
        _mode.SelectionChanged += (_, _) =>
            settings.MatchMode = _mode.SelectedIndex == 1 ? "contains" : "exact";
        _source.SelectionChanged += (_, _) => settings.Source = _source.SelectedIndex switch
        {
            1 => "roster",
            2 => "text",
            3 => "photo",
            4 => "multi",
            5 => "single",
            _ => "any"
        };
        _remaining.TextChanged += (_, _) =>
            settings.MaxRemaining = int.TryParse(_remaining.Text, out var n) && n > 0 ? n : 0;
    }
}
