using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 随机抽选的设置页。
/// </summary>
/// <remarks>
/// 日常那些开关都在悬浮窗的右键菜单里，这一页主要承载「拍照抽人」——
/// 尤其是<b>「试拍一次」的实测数字</b>：一张几十人的合影到底能检出多少、各步各花多少毫秒，
/// 只能量，不能猜。
/// <para/>
/// <b>数值类设置一律走本页的包装属性，不直接绑到 <see cref="PickerSettings"/> 上。</b>
/// 那个类是普通 POCO，不发变更通知；直接绑的话拖完滑块旁边的数字不会跟着变。
/// 包装属性在写入之后顺手把关联的显示文字一起通知掉，还能立刻落盘。
/// </remarks>
[SettingsPageInfo("gordon.randompicker", "随机抽选", "", "")]
public partial class PickerSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly PickerHostService? _service;

    private List<CameraDevice> _cameras = [];
    private ShotResult? _lastShot;
    private bool _shooting;

    public PickerSettings Settings => _service?.Settings ?? new PickerSettings();

    public PickerSettingsPage()
    {
        _service = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<PickerHostService>()
            .FirstOrDefault();

        DataContext = this;
        InitializeComponent();

        _ = LoadCamerasAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 名单

    public string RosterSummary => _service is null
        ? "插件未就绪。"
        : $"{_service.RosterPath}\n一行一个名字，保存后立即生效。";

    #endregion

    #region 抽选动画

    /// <summary>动画样式下拉框的选项文案。</summary>
    /// <remarks>顺序必须和 <see cref="RevealAnimationStyle"/> 的枚举值一一对应，改了要两边一起改。</remarks>
    public List<string> AnimationStyleNames { get; } =
        ["无动画", "滚动名字", "CSGO 开箱", "老虎机", "拼多多转盘"];

    /// <summary>下拉框当前选中的下标。</summary>
    public int AnimationStyleIndex
    {
        // 枚举值本身就是下标。
        get => (int)Settings.AnimationStyle;
        set
        {
            // 喵~防御：下拉框理论上不会给出越界下标，但配置被手改坏时可能，直接忽略。
            if (value < 0 || value > (int)RevealAnimationStyle.Wheel)
            {
                return;
            }

            Settings.AnimationStyle = (RevealAnimationStyle)value;
            // 存盘并刷新说明文字，以及四个时长滑块各自的可用状态。
            Save(nameof(AnimationSummary),
                nameof(IsScrollStyle), nameof(IsCsgoStyle), nameof(IsSlotStyle), nameof(IsWheelStyle));
        }
    }

    /// <summary>当前选的是不是滚动名字（决定对应那个时长滑块能不能拖）。</summary>
    public bool IsScrollStyle => Settings.AnimationStyle == RevealAnimationStyle.Scroll;

    /// <summary>当前选的是不是 CSGO 开箱。</summary>
    public bool IsCsgoStyle => Settings.AnimationStyle == RevealAnimationStyle.Csgo;

    /// <summary>当前选的是不是老虎机。</summary>
    public bool IsSlotStyle => Settings.AnimationStyle == RevealAnimationStyle.Slot;

    /// <summary>当前选的是不是转盘。</summary>
    public bool IsWheelStyle => Settings.AnimationStyle == RevealAnimationStyle.Wheel;

    /// <summary>样式下面那行说明。</summary>
    public string AnimationSummary => Settings.AnimationStyle switch
    {
        RevealAnimationStyle.None => "抽完直接出结果，没有动画。",
        RevealAnimationStyle.Scroll => "中央大字快速换名字，线性减速后定格。",
        RevealAnimationStyle.Csgo => "名字方块横向滚过，指针停在中间那一个上。",
        RevealAnimationStyle.Slot => "逐格抽字，每格从还有可能的字里挑。名单里一旦有超过四个字的名字，会自动改用滚动名字。",
        RevealAnimationStyle.Wheel => "指针先停在两个名字的缝上，再滑进中选的那一格。",
        _ => string.Empty
    };

    /// <summary>滚动名字的时长，单位：秒。</summary>
    public double ScrollDuration
    {
        get => Settings.ScrollDurationSeconds;
        set
        {
            // 保留一位小数，滑块的刻度就是 0.5。
            Settings.ScrollDurationSeconds = Math.Round(value, 1);
            Save(nameof(ScrollDurationText));
        }
    }

    /// <summary>滚动名字时长的显示文字。</summary>
    public string ScrollDurationText => $"{Settings.ScrollDurationSeconds:F1} 秒";

    /// <summary>CSGO 开箱的时长，单位：秒。</summary>
    public double CsgoDuration
    {
        get => Settings.CsgoDurationSeconds;
        set
        {
            Settings.CsgoDurationSeconds = Math.Round(value, 1);
            Save(nameof(CsgoDurationText));
        }
    }

    /// <summary>CSGO 开箱时长的显示文字。</summary>
    public string CsgoDurationText => $"{Settings.CsgoDurationSeconds:F1} 秒";

    /// <summary>老虎机每一格的时长，单位：秒。</summary>
    public double SlotStep
    {
        get => Settings.SlotStepSeconds;
        set
        {
            Settings.SlotStepSeconds = Math.Round(value, 1);
            Save(nameof(SlotStepText));
        }
    }

    /// <summary>老虎机每格时长的显示文字。</summary>
    public string SlotStepText => $"{Settings.SlotStepSeconds:F1} 秒/格";

    /// <summary>转盘转到边界的时长，单位：秒。</summary>
    public double WheelDuration
    {
        get => Settings.WheelDurationSeconds;
        set
        {
            Settings.WheelDurationSeconds = Math.Round(value, 1);
            Save(nameof(WheelDurationText));
        }
    }

    /// <summary>转盘时长的显示文字。</summary>
    public string WheelDurationText => $"{Settings.WheelDurationSeconds:F1} 秒";

    /// <summary>CSGO 动画里各档品质的名字，给设置页那几个滑块当标题用。</summary>
    /// <remarks>顺序必须和 <see cref="ItemRarity"/> 的枚举一致，改了要两边一起改。</remarks>
    public List<string> RarityNames { get; } =
        ItemRarityTable.Tiers.Select(tier => tier.Name).ToList();

    /// <summary>某一档品质的默认权重（百分数）。</summary>
    /// <param name="index">档位下标，0 是最常见的军规级。</param>
    public double RarityWeightOf(int index) => ItemRarityTable.DefaultWeightOf((ItemRarity)index);

    /// <summary>某一档品质当前配的权重（百分数）。</summary>
    /// <param name="index">档位下标。</param>
    /// <remarks>
    /// 喵~防御：配置被手改坏、<c>RarityWeights</c> 长度不足时返回默认值，
    /// 而不是让设置页直接抛异常打不开。
    /// </remarks>
    public double RarityWeight(int index) =>
        Settings.RarityWeights is { } weights && index >= 0 && index < weights.Count
            ? weights[index]
            : RarityWeightOf(index);

    /// <summary>把某一档的权重写回去。</summary>
    /// <param name="index">档位下标。</param>
    /// <param name="value">新的权重（百分数）。</param>
    /// <remarks>
    /// 五个滑块各自独立写入，不做归一化：用户可以把总数拖成任何值，
    /// 抽的时候按相对比例算。这样调概率时不用一边拖一边心算总和。
    /// </remarks>
    public void SetRarityWeight(int index, double value)
    {
        // 喵~防御：越界下标直接忽略，别把整个列表改坏。
        if (index < 0 || index >= ItemRarityTable.Tiers.Count)
        {
            return;
        }

        // 配置里的列表长度不足时先补齐，免得后面按下标写入时越界。
        while (Settings.RarityWeights.Count <= index)
        {
            Settings.RarityWeights.Add(0);
        }

        // 保留两位小数：0.26% 这种值只留一位就没了。
        Settings.RarityWeights[index] = Math.Round(value, 2);
        Save(nameof(RaritySummary), nameof(RarityWeight0), nameof(RarityWeight1),
            nameof(RarityWeight2), nameof(RarityWeight3), nameof(RarityWeight4));
    }

    /// <summary>各档权重的显示值，给界面绑定用。</summary>
    public double RarityWeight0
    {
        get => RarityWeight(0);
        set => SetRarityWeight(0, value);
    }

    /// <summary>第二档（受限级）的权重。</summary>
    public double RarityWeight1
    {
        get => RarityWeight(1);
        set => SetRarityWeight(1, value);
    }

    /// <summary>第三档（保密级）的权重。</summary>
    public double RarityWeight2
    {
        get => RarityWeight(2);
        set => SetRarityWeight(2, value);
    }

    /// <summary>第四档（隐秘级）的权重。</summary>
    public double RarityWeight3
    {
        get => RarityWeight(3);
        set => SetRarityWeight(3, value);
    }

    /// <summary>第五档（罕见特殊物品）的权重。</summary>
    public double RarityWeight4
    {
        get => RarityWeight(4);
        set => SetRarityWeight(4, value);
    }

    /// <summary>各档权重的显示文字。</summary>
    public List<string> RarityWeightTexts =>
        ItemRarityTable.Tiers
            .Select((tier, index) => $"{tier.Name} {RarityWeight(index):0.##}%")
            .ToList();

    /// <summary>品质那一段下面的说明。</summary>
    public string RaritySummary
    {
        get
        {
            // 把当前权重按相对比例换算成「每几抽见一次」，比一串百分数直观得多。
            var buckets = ItemRarityTable.ToBuckets(Settings.RarityWeights);
            var total = buckets.Sum();
            if (total <= 0)
            {
                // 全是 0 的时候抽不出来，抽的时候会退回最常见那一档。
                return "五个权重全是 0，抽的时候会一律按军规级处理。";
            }

            // 逐档算出「平均几抽见一次」。
            var parts = ItemRarityTable.Tiers
                .Select((tier, index) => buckets[index] <= 0
                    ? $"{tier.Name} 不出"
                    : $"{tier.Name} 约 1/{total / (double)buckets[index]:0} 抽")
                .ToList();

            return "只影响方块颜色，不影响抽到谁。" + string.Join("　", parts);
        }
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
        // 打个标记：这次落盘是自己引起的，回声不用再刷一遍自己。
        // 不这么做的话，拖滑块时每动一格都会触发一次「重读全部设置」，
        // 滑块会被刚落盘的四舍五入值拽回去，手感一顿一顿的。
        _savingFromPage = true;
        try
        {
            _service?.SaveSettings();
        }
        finally
        {
            _savingFromPage = false;
        }

        Raise(alsoChanged);
    }

    /// <summary>自己正在写盘。</summary>
    private bool _savingFromPage;

    /// <summary>设置从别处（悬浮钮右键菜单）被改了。</summary>
    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        // 自己改的不算「别处」，跳过——理由见 <see cref="Save"/>。
        if (_savingFromPage)
        {
            return;
        }

        // 把和设置有关的那几个绑定全部重读一遍。
        // 界面是按 PropertyChanged 重读的，所以这里只要喊一声，控件就会自己回去取值。
        Raise(nameof(AnimationStyleIndex), nameof(AnimationSummary),
            nameof(IsScrollStyle), nameof(IsCsgoStyle), nameof(IsSlotStyle), nameof(IsWheelStyle),
            nameof(ScrollDuration), nameof(ScrollDurationText),
            nameof(CsgoDuration), nameof(CsgoDurationText),
            nameof(SlotStep), nameof(SlotStepText),
            nameof(WheelDuration), nameof(WheelDurationText),
            nameof(RarityWeight0), nameof(RarityWeight1), nameof(RarityWeight2),
            nameof(RarityWeight3), nameof(RarityWeight4), nameof(RaritySummary),
            nameof(RosterSummary), nameof(SeparateTextRoster), nameof(SavePhotos), nameof(UseTiled));
    }

    /// <summary>
    /// 页面挂进可视树时开始听设置改动。
    /// </summary>
    /// <remarks>
    /// 在挂载/卸载里订阅而不是构造函数里，是因为设置窗口可能反复开关、重建页面实例。
    /// 在构造函数里订阅而不取消，会让已经作废的页面一直被宿主服务引用着，是一次真泄漏。
    /// </remarks>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_service is not null)
        {
            _service.SettingsSaved += OnSettingsSaved;
        }
    }

    /// <summary>页面离开可视树时停止监听。</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_service is not null)
        {
            _service.SettingsSaved -= OnSettingsSaved;
        }

        base.OnDetachedFromVisualTree(e);
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
