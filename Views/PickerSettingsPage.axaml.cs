using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Automation;
using ClassIsland.RandomPicker.Automation;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rule = ClassIsland.Core.Models.Ruleset.Rule;
using RuleGroup = ClassIsland.Core.Models.Ruleset.RuleGroup;
using Ruleset = ClassIsland.Core.Models.Ruleset.Ruleset;

namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 名单权重编辑器里的一行。滑条双向绑到 <see cref="WeightValue"/>，
/// 旁边的数值文本随动，右侧按钮一键恢复名单里写的权重。
/// </summary>
/// <remarks>行自己实现 <see cref="INotifyPropertyChanged"/>：滑条一拖，
/// 数值文本立刻跟着刷新，不依赖整表重建。</remarks>
public sealed class WeightRow : INotifyPropertyChanged
{
    private readonly PickerSettings _settings;
    private readonly Action _save;
    private readonly double _inlineWeight;

    public WeightRow(PickerSettings settings, Action save, int index, string name, double inlineWeight)
    {
        _settings = settings;
        _save = save;
        Index = index;
        Name = name;
        _inlineWeight = inlineWeight;
    }

    public int Index { get; }

    public string Name { get; }

    /// <summary>名单文件里写的权重，给这一行做个说明。</summary>
    public string InlineHint => Math.Abs(_inlineWeight - 1.0) < 0.001
        ? "默认 1"
        : $"名单 *{_inlineWeight.ToString("G", CultureInfo.InvariantCulture)}";

    /// <summary>滑条绑定的权重值（0.1 ~ 10）。拖完立刻写盘。</summary>
    public double WeightValue
    {
        get => _settings.GetWeight(Name, _inlineWeight);
        set
        {
            value = Math.Round(Math.Clamp(value, 0.1, 10.0), 2);
            if (Math.Abs(value - _settings.GetWeight(Name, _inlineWeight)) < 0.005)
            {
                return;
            }

            _settings.SetWeight(Name, value);
            _save();
            Raise(nameof(WeightValue), nameof(WeightText));
        }
    }

    /// <summary>当前生效的权重，显示用。和 <see cref="WeightValue"/> 同步刷新。</summary>
    public string WeightText => _settings.GetWeight(Name, _inlineWeight)
        .ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>删掉本行的覆盖值，回落到名单文件里写的权重。</summary>
    public void Reset()
    {
        _settings.SetWeight(Name, null);
        _save();
        Raise(nameof(WeightValue), nameof(WeightText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>
/// 随机抽选的设置页。
/// </summary>
/// <remarks>
/// 日常那些开关都在悬浮窗的右键菜单里，这一页承载：名单权重、悬浮窗显示、
/// URL / 本地接口、自动化示例，以及「拍照抽人」的实测数字。
/// <para/>
/// <b>数值类设置一律走本页的包装属性，不直接绑到 <see cref="PickerSettings"/> 上。</b>
/// 那个类是普通 POCO，不发变更通知；直接绑的话拖完滑块旁边的数字不会跟着变。
/// </remarks>
[SettingsPageInfo("gordon.randompicker", "随机抽选", "\uD83C\uDFB2", "\uD83C\uDFB2")]
public partial class PickerSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly PickerHostService? _service;

    private List<CameraDevice> _cameras = [];
    private ShotResult? _lastShot;
    private bool _shooting;
    private string _sampleSummary = "可以直接在「应用设置 → 自动化」里选用本插件的触发器、行动和规则。";

    public PickerSettings Settings => _service?.Settings ?? new PickerSettings();

    public PickerSettingsPage()
    {
        _service = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<PickerHostService>()
            .FirstOrDefault();

        DataContext = this;
        InitializeComponent();

        BuildWeightRows();
        SyncHttpFields();
        _ = LoadCamerasAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 常规与名单

    /// <summary>悬浮窗是否显示。关掉之后可以从这里随时叫回来，不用重启 ClassIsland。</summary>
    public bool FloatingWindowVisible
    {
        get => !Settings.HideFloatingWindow;
        set
        {
            Settings.HideFloatingWindow = !value;
            _service?.SetFloatingWindowVisible(value);
            Raise(nameof(FloatingWindowVisible), nameof(FloatingWindowSummary));
        }
    }

    public string FloatingWindowSummary => FloatingWindowVisible
        ? "悬浮窗正在显示。也可以在悬浮窗右键菜单里选「隐藏悬浮窗」。"
        : "悬浮窗已隐藏。打开上面的开关即可叫回来，无需重启 ClassIsland。";

    /// <summary>悬浮窗缩放系数（0.5 ~ 2.0），乘在尺寸档位的直径上。</summary>
    public double FloatingScale
    {
        get => Settings.FloatingWindowScale;
        set
        {
            Settings.FloatingWindowScale = Math.Clamp(Math.Round(value, 2), 0.5, 2.0);
            Save(nameof(FloatingScaleText));
            _service?.ApplyFloatingScale();
        }
    }

    public string FloatingScaleText => $"{Settings.FloatingWindowScale:F2}×";

    /// <summary>课间禁用所有点名功能。</summary>
    public bool DisableDuringBreak
    {
        get => Settings.DisableDuringBreak;
        set
        {
            Settings.DisableDuringBreak = value;
            Save();
        }
    }

    /// <summary>打断上一次点名并触发新的点名；关闭时上一次播完前新的请求会被忽略。</summary>
    public bool InterruptPreviousPick
    {
        get => Settings.InterruptPreviousPick;
        set
        {
            Settings.InterruptPreviousPick = value;
            Save();
        }
    }

    #region 快捷键

    /// <summary>Win32 MOD_* 位掩码对应的可选组合。与 <see cref="ModifierMasks"/> 一一对应。</summary>
    public List<string> HotkeyModifierChoices { get; } =
    [
        "Ctrl + Alt", "Ctrl + Shift", "Shift + Alt", "Win + Alt", "Ctrl + Shift + Alt"
    ];

    private static readonly int[] ModifierMasks = [3, 6, 5, 9, 7];

    /// <summary>可选的触发热键的按键。与 <see cref="KeyCodes"/> 一一对应。</summary>
    public List<string> HotkeyKeyChoices { get; } = ["R", "P", "H", "M", "K", "空格"];

    private static readonly int[] KeyCodes = [0x52, 0x50, 0x48, 0x4D, 0x4B, 0x20];

    public bool HotkeyEnabled
    {
        get => Settings.HotkeyEnabled;
        set
        {
            Settings.HotkeyEnabled = value;
            Save(nameof(HotkeySummary));
            _service?.ApplyHotkey();
        }
    }

    public int HotkeyModifiersIndex
    {
        get
        {
            var index = Array.IndexOf(ModifierMasks, Settings.HotkeyModifiers);
            return index < 0 ? 0 : index;
        }
        set
        {
            Settings.HotkeyModifiers = ModifierMasks[Math.Clamp(value, 0, ModifierMasks.Length - 1)];
            Save(nameof(HotkeySummary));
            _service?.ApplyHotkey();
        }
    }

    public int HotkeyKeyIndex
    {
        get
        {
            var index = Array.IndexOf(KeyCodes, Settings.HotkeyVirtualKey);
            return index < 0 ? 0 : index;
        }
        set
        {
            Settings.HotkeyVirtualKey = KeyCodes[Math.Clamp(value, 0, KeyCodes.Length - 1)];
            Save(nameof(HotkeySummary));
            _service?.ApplyHotkey();
        }
    }

    public string HotkeySummary
    {
        get
        {
            if (!Settings.HotkeyEnabled)
            {
                return "已关闭。打开开关后，按下快捷键即可在任意界面显示 / 隐藏悬浮窗。";
            }

            var combo = $"{HotkeyModifierChoices[HotkeyModifiersIndex]} + {HotkeyKeyChoices[HotkeyKeyIndex]}";
            return $"当前 {combo}。悬浮窗被隐藏时也能一键叫回来，无需打开设置。热键被其它程序占用时自动失效。";
        }
    }

    #endregion

    public string RosterSummary => _service is null
        ? "插件未就绪。"
        : $"第 {Math.Clamp(Settings.ActiveRosterIndex, 1, PickerHostService.RosterSlots)} 份：{_service.RosterPath}\n" +
          "一行一个名字，保存后立即生效。行尾写 *N 可加权。";

    /// <summary>五份名单的下拉选项，顺带显示各份的人数。</summary>
    public List<string> RosterChoices
    {
        get
        {
            var list = new List<string>();
            for (var i = 1; i <= PickerHostService.RosterSlots; i++)
            {
                list.Add(_service is null ? $"名单 {i}" : _service.RosterLabel(i));
            }

            return list;
        }
    }

    /// <summary>下拉框用的是 0 基下标，设置里存的是 1 基编号。</summary>
    public int RosterIndex
    {
        get => Math.Clamp(Settings.ActiveRosterIndex, 1, PickerHostService.RosterSlots) - 1;
        set
        {
            var index = Math.Clamp(value + 1, 1, PickerHostService.RosterSlots);
            if (Settings.ActiveRosterIndex == index)
            {
                return;
            }

            Settings.ActiveRosterIndex = index;
            _service?.SwitchRoster(index);
            Raise(nameof(RosterIndex), nameof(RosterSummary), nameof(RosterChoices),
                nameof(GroupSummary), nameof(WeightSummary));
            BuildWeightRows();
        }
    }

    /// <summary>小组抽选的每组人数。</summary>
    public int GroupSize
    {
        get => Settings.GroupSize;
        set
        {
            Settings.GroupSize = Math.Clamp(value, GroupService.MinGroupSize, GroupService.MaxGroupSize);
            Save();
            Raise(nameof(GroupSize), nameof(GroupSummary));
        }
    }

    public string GroupSummary
    {
        get
        {
            var count = _service?.Roster?.Names.Count ?? 0;
            var size = Math.Clamp(Settings.GroupSize, GroupService.MinGroupSize,
                GroupService.MaxGroupSize);
            var groups = GroupService.GroupCount(count, size);
            if (groups == 0)
            {
                return "当前名单是空的，先在名单里写几个名字。";
            }

            var last = count - (groups - 1) * size;
            return $"当前名单 {count} 人，每 {size} 人一组，共 {groups} 组，最后一组 {last} 人。";
        }
    }

    /// <summary>名单权重总开关。</summary>
    public bool EnableNameWeights
    {
        get => Settings.EnableNameWeights;
        set
        {
            Settings.EnableNameWeights = value;
            Save(nameof(WeightSummary));
            BuildWeightRows();
        }
    }

    public List<WeightRow> WeightRows { get; private set; } = [];

    public string WeightSummary
    {
        get
        {
            var total = _service?.Roster?.Names.Count ?? 0;
            var overridden = Settings.NameWeights.Count;
            var inline = _service?.Roster?.Entries.Count(x => Math.Abs(x.Weight - 1.0) > 0.001) ?? 0;
            return $"共 {total} 人；{overridden} 人在这里单独设过权重，{inline} 人在名单文件里写了 *N。" +
                   (Settings.EnableNameWeights ? "" : "\n权重总开关已关闭，目前所有人等概率。");
        }
    }

    /// <summary>按当前名单重建权重行。</summary>
    private void BuildWeightRows()
    {
        var entries = _service?.Roster?.Entries ?? Array.Empty<RosterEntry>();
        WeightRows = entries
            .Select((x, i) => new WeightRow(Settings, () => Save(nameof(WeightSummary)),
                i + 1, x.Name, x.Weight))
            .ToList();
        Raise(nameof(WeightRows), nameof(WeightSummary));
    }

    private void OnRefreshWeights(object? sender, RoutedEventArgs e)
    {
        _service?.ReloadRoster();
        BuildWeightRows();
        Raise(nameof(RosterChoices), nameof(GroupSummary));
    }

    private void OnResetWeights(object? sender, RoutedEventArgs e)
    {
        Settings.ClearWeights();
        Save();
        BuildWeightRows();
    }

    /// <summary>单行权重旁的恢复按钮：删掉这一个人的覆盖值。</summary>
    private void OnResetRowWeight(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Button)?.Tag is WeightRow row)
        {
            row.Reset();
        }
    }

    private void OnOpenRoster(object? sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_service.RosterPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序就算了，路径就写在上面。
        }
    }

    #endregion

    #region URL 与本地接口

    public bool EnableUriPick
    {
        get => Settings.EnableUriPick;
        set
        {
            Settings.EnableUriPick = value;
            Save(nameof(UriSummary));
        }
    }

    public string UriSummary => Settings.EnableUriPick
        ? "已启用。用浏览器、快捷方式或命令行打开下面的 classisland:// 链接即可抽人。"
        : "已关闭。classisland:// 链接不会触发抽选（本地 HTTP 接口是独立开关）。";

    public string UriExamples =>
        "classisland://plugins/randompicker/pick                 按当前模式抽一人\n" +
        "classisland://plugins/randompicker/roster?count=3      名单一次抽 3 人\n" +
        "classisland://plugins/randompicker/group               抽一个小组\n" +
        "classisland://plugins/randompicker/photo               拍照抽一人\n" +
        "classisland://plugins/randompicker/photo?count=2       拍照一次抽 2 人\n" +
        "classisland://plugins/randompicker/multi               一次抽多人（人数见设置）\n" +
        "classisland://plugins/randompicker/show                显示悬浮窗\n" +
        "classisland://plugins/randompicker/hide                隐藏悬浮窗\n" +
        "classisland://plugins/randompicker/toggle              切换悬浮窗显隐\n" +
        "classisland://plugins/randompicker/pick?kind=roster&roster=text&count=2\n" +
        "                                                      用「名单-文字.txt」抽 2 人\n" +
        "查询参数：kind=auto|roster|photo|group、mode=random|norepeat|photo|group、\n" +
        "          count=人数、roster=default|main|text、reveal/notify=0|1";

    public bool EnableHttpApi
    {
        get => Settings.EnableHttpApi;
        set
        {
            Settings.EnableHttpApi = value;
            Save(nameof(HttpStatus));
            _service?.RestartHttpApi();
            Raise(nameof(HttpStatus));
        }
    }

    /// <summary>端口用字符串绑，避免逐字符输入时反复重启监听。</summary>
    public string HttpApiPortText { get; set; } = "17890";

    public string HttpApiTokenText { get; set; } = string.Empty;

    public string HttpStatus
    {
        get
        {
            var api = _service?.HttpApi;
            if (!Settings.EnableHttpApi)
            {
                return "未开启。只监听 127.0.0.1，不会暴露到局域网。";
            }

            if (api is null)
            {
                return "插件未就绪。";
            }

            return api.IsRunning
                ? $"正在监听 http://127.0.0.1:{Settings.HttpApiPort}/randompicker/…"
                : (api.LastError ?? "未启动。");
        }
    }

    public string HttpExamples =>
        "http://127.0.0.1:17890/randompicker/roster?count=3\n" +
        "http://127.0.0.1:17890/randompicker/photo?count=2\n" +
        "http://127.0.0.1:17890/randompicker/group\n" +
        "http://127.0.0.1:17890/randompicker/status\n" +
        "设了令牌就加 &token=你的令牌（或 X-Picker-Token 请求头）。";

    private void SyncHttpFields()
    {
        HttpApiPortText = Settings.HttpApiPort.ToString(CultureInfo.InvariantCulture);
        HttpApiTokenText = Settings.HttpApiToken;
        Raise(nameof(HttpApiPortText), nameof(HttpApiTokenText), nameof(HttpStatus));
    }

    private void OnRestartHttp(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HttpApiPortText, out var port) || port is < 1024 or > 65535)
        {
            port = 17890;
            HttpApiPortText = port.ToString(CultureInfo.InvariantCulture);
        }

        Settings.HttpApiPort = port;
        Settings.HttpApiToken = HttpApiTokenText?.Trim() ?? string.Empty;
        Save();
        _service?.RestartHttpApi();
        SyncHttpFields();
    }

    #endregion

    #region 自动化 / 规则集

    public string AutomationSummary =>
        "触发器：抽中人选时、名单抽人时、拍照抽人时、一次抽多人时、当抽中指定名字时、当悬浮窗隐藏时、当悬浮窗显示时。\n" +
        "行动：抽选一次、名单抽人、拍照抽人、一次抽多人、抽一个小组、隐藏悬浮窗、显示悬浮窗（在「添加行动 → 随机抽选」）。\n" +
        "规则：最近抽中的人是、最近一次抽选方式是、本轮剩余人数不超过。\n" +
        "把「触发器 + 规则集 + 行动」组合起来就是一条自动化工作流。";

    public string SampleSummary
    {
        get => _sampleSummary;
        private set
        {
            _sampleSummary = value;
            Raise(nameof(SampleSummary));
        }
    }

    /// <summary>
    /// 往当前自动化配置里加一条示例工作流（含规则集）。
    /// </summary>
    /// <remarks>
    /// 默认把行动组设为<b>不启用</b>，避免用户还没看清就真的开始连锁抽人；
    /// 想用的时候在自动化设置里打开即可。重复点不会加第二份。
    /// </remarks>
    private void OnAddSampleWorkflow(object? sender, RoutedEventArgs e)
    {
        var automation = IAppHost.Host?.Services.GetService<IAutomationService>();
        if (automation is null)
        {
            SampleSummary = "自动化服务还没就绪，稍后再试。";
            return;
        }

        const string workflowName = "随机抽选 · 示例规则集";
        if (automation.Workflows.Any(x => x.ActionSet.Name == workflowName))
        {
            SampleSummary = "示例规则集已经在自动化里了，去「应用设置 → 自动化」查看。";
            return;
        }

        var workflow = new Workflow
        {
            IsConditionEnabled = true,
            ActionSet = new ActionSet
            {
                Name = workflowName,
                IsEnabled = false,
                ActionItems =
                [
                    new ActionItem
                    {
                        Id = "gordon.randompicker.action.roster",
                        Settings = new PickerActionSettings { Count = 1 }
                    }
                ]
            },
            Ruleset = new Ruleset
            {
                Groups =
                [
                    new RuleGroup
                    {
                        Rules =
                        [
                            new Rule
                            {
                                Id = PickerRules.LastNameId,
                                Settings = new PickerRuleSettings { Name = "张三" }
                            }
                        ]
                    }
                ]
            }
        };

        workflow.Triggers.Add(new TriggerSettings
        {
            Id = "gordon.randompicker.trigger.pickedName",
            Settings = new PickerNameMatchSettings { Name = "张三" }
        });

        automation.Workflows.Add(workflow);
        automation.SaveConfig("添加随机抽选示例规则集");
        SampleSummary = "已添加。到「应用设置 → 自动化」里把行动组启用即可生效（已改成你想要的规则）。";
    }

    #endregion

    #region 摄像头

    public List<string> CameraNames { get; private set; } = [];

    public int CameraIndex
    {
        get
        {
            var index = _cameras.FindIndex(x => x.Id == Settings.CameraDeviceId);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= _cameras.Count)
            {
                return;
            }

            Settings.CameraDeviceId = _cameras[value].Id;
            Settings.CameraDeviceName = _cameras[value].Name;
            Save(nameof(CameraSummary));
        }
    }

    public string CameraSummary => _cameras.Count == 0
        ? "未检测到摄像头。需在系统设置中允许桌面应用访问相机。"
        : $"检测到 {_cameras.Count} 个摄像头，拍摄使用其最高分辨率。";

    /// <summary>抽完之后摄像头继续开着多少秒。</summary>
    public double KeepAlive
    {
        get => Settings.CameraKeepAliveSeconds;
        set
        {
            Settings.CameraKeepAliveSeconds = (int)Math.Round(value);
            Save(nameof(KeepAliveText), nameof(KeepAliveSummary));
        }
    }

    public string KeepAliveText => Settings.CameraKeepAliveSeconds <= 0
        ? "用后即关"
        : $"{Settings.CameraKeepAliveSeconds} 秒";

    public string KeepAliveSummary =>
        "开启摄像头约需一两秒，保持开启可加快连续抽取。期间指示灯常亮，超时自动关闭。" +
        (CameraPicker.IsWarm ? "\n当前:已开启。" : "\n当前:未开启。");

    #endregion

    #region 检测

    public double Threshold
    {
        get => Settings.FaceScoreThreshold;
        set
        {
            Settings.FaceScoreThreshold = Math.Round(value, 2);
            Save(nameof(ThresholdText));
        }
    }

    public string ThresholdText => $"{Settings.FaceScoreThreshold:F2}";

    public double TileGrid
    {
        get => Settings.TileGrid;
        set
        {
            Settings.TileGrid = (int)Math.Round(value);
            Save(nameof(TileGridText));
        }
    }

    public string TileGridText => $"{Settings.TileGrid}×{Settings.TileGrid}";

    public double AvoidRecent
    {
        get => Settings.PhotoAvoidRecent;
        set
        {
            Settings.PhotoAvoidRecent = (int)Math.Round(value);
            CameraPicker.ForgetRecent();
            Save(nameof(AvoidRecentText));
        }
    }

    public string AvoidRecentText =>
        Settings.PhotoAvoidRecent <= 0 ? "不回避" : $"{Settings.PhotoAvoidRecent} 人";

    public double TextChance
    {
        get => Settings.PhotoTextChance;
        set
        {
            Settings.PhotoTextChance = (int)Math.Round(value);
            Save(nameof(TextChanceText));
        }
    }

    public string TextChanceText =>
        Settings.PhotoTextChance <= 0 ? "不混入" : $"{Settings.PhotoTextChance}%";

    public bool SeparateTextRoster
    {
        get => Settings.SeparateTextRoster;
        set
        {
            Settings.SeparateTextRoster = value;
            Save();
        }
    }

    public double CropWidth
    {
        get => Settings.CropWidthFactor;
        set
        {
            Settings.CropWidthFactor = Math.Round(value, 1);
            Save(nameof(CropText));
        }
    }

    public double CropHeight
    {
        get => Settings.CropHeightFactor;
        set
        {
            Settings.CropHeightFactor = Math.Round(value, 1);
            Save(nameof(CropText));
        }
    }

    public string CropText => $"{Settings.CropWidthFactor:F1}× / {Settings.CropHeightFactor:F1}×";

    public bool UseTiled
    {
        get => Settings.UseTiledDetection;
        set
        {
            Settings.UseTiledDetection = value;
            Save(nameof(UseTiled));
        }
    }

    public bool SavePhotos
    {
        get => Settings.SavePhotos;
        set
        {
            Settings.SavePhotos = value;
            Save(nameof(SavePhotos));
        }
    }

    #endregion

    #region 试拍

    public bool CanShoot => !_shooting && _cameras.Count > 0;

    /// <summary>试拍结果。这里的数字就是判断「能不能用」的全部依据。</summary>
    public string ShotSummary
    {
        get
        {
            if (_shooting)
            {
                return "正在拍摄……";
            }

            if (_lastShot is null)
            {
                return "拍摄一张，检查检测效果。";
            }

            return (_lastShot.Success ? "✓ " : "⚠ ") + _lastShot.Message + "\n" + _lastShot.Diagnostics;
        }
    }

    public Bitmap? Preview => _lastShot?.Annotated;

    public bool HasPreview => _lastShot?.Annotated is not null;

    #endregion

    private async Task LoadCamerasAsync()
    {
        var found = await CameraPicker.ListCamerasAsync();
        Dispatcher.UIThread.Post(() =>
        {
            _cameras = found;
            CameraNames = found.Select(x => x.Name).ToList();
            Raise(nameof(CameraNames), nameof(CameraIndex), nameof(CameraSummary), nameof(CanShoot));
        });
    }

    private void OnRefreshCameras(object? sender, RoutedEventArgs e) => _ = LoadCamerasAsync();

    private void OnCloseCamera(object? sender, RoutedEventArgs e) =>
        _ = CameraPicker.ShutdownCameraAsync().ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => Raise(nameof(KeepAliveSummary))));

    private async void OnTestShot(object? sender, RoutedEventArgs e)
    {
        if (_service is null || _shooting)
        {
            return;
        }

        _shooting = true;
        _lastShot = null;
        Raise(nameof(ShotSummary), nameof(CanShoot), nameof(Preview), nameof(HasPreview));

        try
        {
            // annotate: true —— 把检测框画在缩略图上，「漏了谁」是看得见的，不用凭数字猜。
            _lastShot = await CameraPicker.CaptureAndPickAsync(
                Settings, PickerHostService.PluginDirectory, _service.ConfigFolder, annotate: true);
        }
        catch (Exception ex)
        {
            _lastShot = new ShotResult { Success = false, Message = ex.Message };
        }
        finally
        {
            _shooting = false;
            Raise(nameof(ShotSummary), nameof(CanShoot), nameof(Preview), nameof(HasPreview),
                nameof(KeepAliveSummary));
        }
    }

    /// <summary>写盘并通知界面。数值类设置改完都走这儿。</summary>
    private void Save(params string[] alsoChanged)
    {
        _service?.SaveSettings();
        Raise(alsoChanged);
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
}
