using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// 和悬浮球右键菜单是<b>同一批设置</b>，改哪边都一样——菜单适合上课时随手点，
/// 这一页适合一次配好。所以来源、抽取数量、放回/不放回、大小、结果显示这些都在这里复刻了一份，
/// 排在「拍照抽人」前面。
/// <para/>
/// 此外还承载拍照抽人的详细参数，尤其是<b>「试拍一次」的实测数字</b>：
/// 一张几十人的合影到底能检出多少、各步各花多少毫秒，只能量，不能猜。
/// <para/>
/// <b>数值类设置一律走本页的包装属性，不直接绑到 <see cref="PickerSettings"/> 上。</b>
/// 那个类是普通 POCO，不发变更通知；直接绑的话拖完滑块旁边的数字不会跟着变。
/// 包装属性在写入之后顺手把关联的显示文字一起通知掉，还能立刻落盘。
/// </remarks>
/// <remarks>
/// 侧边栏图标是 Fluent 的 <c>arrow_shuffle</c>（交叉箭头）：一组图标里它最接近「随机」这个意思，
/// 抽人、抽组、拍照三种来源都盖得住。未选中用 regular（<c>\ue13b</c>）、选中用 filled
/// （<c>\ue13a</c>）——宿主自己那几十个设置页都是这个配对规则。
/// </remarks>
[SettingsPageInfo(SettingsPageId, "随机抽选", "\ue13b", "\ue13a")]
public partial class PickerSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    /// <summary>
    /// 设置页 id。悬浮球右键菜单里的「打开插件设置」按它拼 uri，两边必须一致。
    /// </summary>
    public const string SettingsPageId = "gordon.randompicker";
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

        // 先把下拉框的内容填好再挂 DataContext：反过来的话 ComboBox 会先拿到空集合，
        // SelectedIndex 被夹成 -1 再写回设置。
        RefreshListNames();

        DataContext = this;
        InitializeComponent();

        _ = LoadCamerasAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 悬浮窗

    /// <summary>
    /// 悬浮窗是否显示。
    /// </summary>
    /// <remarks>
    /// 藏起来之后正常是从托盘菜单「更多选项 → 显示抽选悬浮球」叫回来。这里留第二条路，
    /// 免得托盘菜单万一没注册上时，用户彻底找不到把悬浮窗叫回来的入口。
    /// </remarks>
    public bool PickerVisible
    {
        get => !Settings.Hidden;
        set
        {
            _service?.SetPickerWindowVisible(value);
            Raise(nameof(PickerVisible), nameof(PickerVisibleText));
        }
    }

    public string PickerVisibleText => Settings.Hidden
        ? "已隐藏。可在托盘菜单「更多选项 → 显示抽选悬浮球」叫回来。"
        : "正在显示。隐藏后不会自己冒出来，重启也保持隐藏。";

    public List<string> SizeNames { get; } = ["小", "中", "大"];

    /// <summary>悬浮球大小。0 / 1 / 2 对应小 / 中 / 大。</summary>
    public int BallSizeIndex
    {
        get => (int)Settings.Size;
        set
        {
            if (value is < 0 or > 2)
            {
                return;
            }

            Settings.Size = (PickerSize)value;
            // 右键菜单改完是当场 ApplySize 的，设置页也得立刻刷，不然要等下次重开。
            _service?.RefreshPickerWindow();
            Save(nameof(BallSizeIndex));
        }
    }

    /// <summary>悬浮球上是否显示人数。</summary>
    public bool ShowPickerCount
    {
        get => Settings.ShowPickerCount;
        set
        {
            Settings.ShowPickerCount = value;
            // 球上那行小字要立刻换掉，不然得等下次重开窗口。
            _service?.RefreshPickerWindow();
            Save(nameof(ShowPickerCount), nameof(PickerCountSummary));
        }
    }

    public string PickerCountSummary => Settings.ShowPickerCount
        ? "球上显示「还剩几个 / 共几个」，抽人和抽组会分别标人、组。"
        : "球上改显示「放回 / 不放回」。拍照模式仍显示「拍照」。";

    #endregion

    #region 抽人

    public List<string> SourceNames { get; } = ["拍照抽人", "随机抽人", "随机抽组"];

    public List<string> CountNames { get; } = ["1 个", "2 个", "3 个"];

    public List<string> ReplaceNames { get; } = ["不放回", "放回"];

    /// <summary>抽人来源。0 = 拍照抽人，1 = 随机抽人，2 = 随机抽组。</summary>
    public int SourceIndex
    {
        get => Settings.Mode switch
        {
            PickMode.Photo => 0,
            PickMode.Group => 2,
            _ => 1
        };
        set
        {
            Settings.SetSource(value switch
            {
                0 => PickMode.Photo,
                2 => PickMode.Group,
                _ => Settings.TextMode
            });
            _service?.RefreshPickerWindow();
            // 只通知真正变了的：抽取模式的值不受来源影响，跟着一起通知会让下拉框来回写。
            Save(nameof(SourceIndex), nameof(SourceSummary), nameof(RoundSummary));
        }
    }

    public string SourceSummary => Settings.Mode switch
    {
        PickMode.Photo => "从摄像头拍的合影里挑人脸，裁出人像。",
        PickMode.Group => "从小组里随机抽一个，比如「C 组」。小组不读名单，按下面的分组设置算出来。",
        _ => "从名单里抽名字。"
    };

    /// <summary>一次抽几个。</summary>
    public int PickCountIndex
    {
        get => Math.Clamp(Settings.PickCount, 1, 3) - 1;
        set
        {
            Settings.PickCount = Math.Clamp(value, 0, 2) + 1;
            Save(nameof(PickCountIndex), nameof(CountSummary));
        }
    }

    public string CountSummary => Settings.PickCount <= 1
        ? "一次抽一个。"
        : $"一次抽 {Settings.PickCount} 个，同一批内不会重复。";

    /// <summary>放回 / 不放回。0 = 不放回，1 = 放回。</summary>
    public int ReplaceIndex
    {
        get => Settings.TextMode == PickMode.NoRepeat ? 0 : 1;
        set
        {
            Settings.SetReplace(value == 1 ? PickMode.Random : PickMode.NoRepeat);
            _service?.RefreshPickerWindow();
            Save(nameof(ReplaceIndex), nameof(ReplaceSummary), nameof(RoundSummary));
        }
    }

    public string ReplaceSummary => Settings.TextMode == PickMode.NoRepeat
        ? "抽过的不再出现，抽完一轮自动开始新一轮。名单和小组共用这个设定。"
        : "每次都从全体里抽，但最近抽过的会被降权（上一次直接排除）。名单和小组共用这个设定。";

    public string RoundSummary
    {
        get
        {
            if (_service is null)
            {
                return "清空本轮记录。";
            }

            if (Settings.Mode == PickMode.Group)
            {
                return Settings.IsNoRepeat
                    ? $"共 {_service.GroupTotal} 组，本轮还剩 {_service.GroupsRemaining} 组。"
                    : $"共 {_service.GroupTotal} 组。放回模式下重置的是降权记录。";
            }

            return Settings.IsNoRepeat
                ? $"共 {_service.RosterCount} 人，本轮还剩 {_service.RemainingInRound} 人。"
                : $"共 {_service.RosterCount} 人。放回模式下重置的是降权记录。";
        }
    }

    #endregion

    #region 抽组

    public List<string> GroupStyleNames { get; } = ["字母", "数字"];

    /// <summary>分组方式。0 = 字母（A 组…），1 = 数字（1 组…）。</summary>
    public int GroupStyleIndex
    {
        get => Settings.GroupStyle == GroupStyle.Letter ? 0 : 1;
        set
        {
            var style = value == 1 ? GroupStyle.Number : GroupStyle.Letter;
            if (style == Settings.GroupStyle)
            {
                return;
            }

            Settings.GroupStyle = style;
            // 换写法等于换了一批标签，「1 组」和「A 组」不是同一个组，本轮记录作废。
            GroupPicker.ResetRound(Settings);
            _service?.RefreshPickerWindow();
            Save(nameof(GroupStyleIndex), nameof(GroupStyleSummary), nameof(GroupSummary),
                nameof(RoundSummary));
        }
    }

    public string GroupStyleSummary => "组名写成「A 组、B 组」还是「1 组、2 组」。改完本轮抽组记录会清空。";

    /// <summary>小组总数，1 ~ 26。</summary>
    public double GroupCount
    {
        get => Settings.GroupCount;
        set
        {
            var count = Math.Clamp((int)Math.Round(value), 1, PickerSettings.MaxGroups);
            if (count == Settings.GroupCount)
            {
                return;
            }

            Settings.GroupCount = count;
            GroupPicker.ResetRound(Settings);
            _service?.RefreshPickerWindow();
            Save(nameof(GroupCount), nameof(GroupCountText), nameof(GroupSummary), nameof(RoundSummary));
        }
    }

    public string GroupCountText => $"{Settings.GroupCount} 组";

    /// <summary>把实际的组名列出来，一眼看出设置成什么样了。</summary>
    public string GroupSummary
    {
        get
        {
            var labels = GroupPicker.Labels(Settings);
            const int show = 6;
            var head = string.Join("、", labels.Take(show));
            return labels.Count > show ? $"{head} …" : head;
        }
    }

    #endregion

    #region 结果显示

    public List<string> HoldNames { get; } = ["1.5 秒", "2.5 秒", "4 秒"];

    /// <summary>菜单里给的三档停留时长。改这里要连 <see cref="HoldNames"/> 一起改。</summary>
    private static readonly double[] HoldSeconds = [1.5, 2.5, 4.0];

    public bool ShowCenterReveal
    {
        get => Settings.ShowCenterReveal;
        set
        {
            Settings.ShowCenterReveal = value;
            Save(nameof(ShowCenterReveal));
        }
    }

    public bool ShowNotification
    {
        get => Settings.ShowNotification;
        set
        {
            Settings.ShowNotification = value;
            Save(nameof(ShowNotification));
        }
    }

    /// <summary>中央弹窗停留时长。老配置里可能是别的值，按最接近的一档显示。</summary>
    public int HoldIndex
    {
        get
        {
            var best = 0;
            for (var i = 1; i < HoldSeconds.Length; i++)
            {
                if (Math.Abs(Settings.RevealSeconds - HoldSeconds[i]) <
                    Math.Abs(Settings.RevealSeconds - HoldSeconds[best]))
                {
                    best = i;
                }
            }

            return best;
        }
        set
        {
            if (value < 0 || value >= HoldSeconds.Length)
            {
                return;
            }

            Settings.RevealSeconds = HoldSeconds[value];
            Save(nameof(HoldIndex), nameof(HoldSummary));
        }
    }

    public string HoldSummary => $"抽中之后停留 {Settings.RevealSeconds:F1} 秒。";

    #endregion

    #region 名单

    /// <summary>
    /// 五份名单在下拉框里怎么显示。
    /// </summary>
    /// <remarks>
    /// 就地替换元素、不换集合实例：换实例会让 ComboBox 的 SelectedIndex 归零，
    /// 而归零又会写回设置，等于把用户选的那份改掉。
    /// </remarks>
    public ObservableCollection<string> ListNames { get; } = [];

    /// <summary>当前选的是第几份（0 起算，对应 list1 ~ list5）。</summary>
    public int ListIndex
    {
        get => Settings.SelectedList - 1;
        set
        {
            if (_service is null || value < 0 || value >= NameListLibrary.SlotCount)
            {
                return;
            }

            if (value + 1 == _service.SelectedList)
            {
                return;
            }

            _service.SwitchList(value + 1);
            RaiseListState();
        }
    }

    public string ListSummary
    {
        get
        {
            if (_service is null)
            {
                return "插件未就绪。";
            }

            var slot = _service.SelectedList;
            var title = _service.Library.PeekTitle(slot);
            var name = NameListLibrary.Label(slot, title, 0);

            return $"当前用的是「{name}」。\n想给名单起名字，在文件开头写一行 @高一5班 就行。";
        }
    }

    public string RosterSummary => _service is null
        ? "插件未就绪。"
        : $"{_service.RosterPath}\n共 {_service.RosterCount} 人，一行一个名字，保存后立即生效。";

    /// <summary>重读五份名单的名字，填进下拉框。</summary>
    private void RefreshListNames()
    {
        while (ListNames.Count < NameListLibrary.SlotCount)
        {
            ListNames.Add($"名单{ListNames.Count + 1}");
        }

        for (var slot = 1; slot <= NameListLibrary.SlotCount; slot++)
        {
            // 下拉框地方够，名字不截断。
            ListNames[slot - 1] = NameListLibrary.Label(slot, _service?.Library.PeekTitle(slot), 0);
        }
    }

    private void RaiseListState() =>
        Raise(nameof(ListIndex), nameof(ListSummary), nameof(RosterSummary), nameof(RoundSummary));

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

    private void OnReloadRoster(object? sender, RoutedEventArgs e)
    {
        _service?.ReloadRoster();
        // 名单名也可能刚被改过，一起重读。
        RefreshListNames();
        Raise(nameof(RosterSummary), nameof(RoundSummary), nameof(ListSummary));
    }

    private void OnResetRound(object? sender, RoutedEventArgs e)
    {
        _service?.ResetRound();
        Raise(nameof(RoundSummary));
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
