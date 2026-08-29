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
