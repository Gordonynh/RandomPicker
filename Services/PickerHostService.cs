using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Models.Notification;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 插件主体：管住悬浮窗、设置、名单，以及抽中之后往哪儿显示。
/// </summary>
public class PickerHostService : IHostedService
{
    private readonly string _settingsPath;
    private readonly string _rosterPath;
    private readonly string _configFolder;

    /// <summary>拍照抽人正在跑。摄像头开一次要一两秒，这期间再点就直接忽略。</summary>
    private bool _shooting;

    private PickerSettings _settings = new();
    private RosterService? _roster;
    private PickerWindow? _window;

    /// <summary>上一条还在播的提醒。连着抽人时先把它取消掉，免得在主界面上排队堆积。</summary>
    private NotificationRequest? _lastRequest;

    public PickerHostService(string pluginConfigFolder)
    {
        _configFolder = pluginConfigFolder;
        _settingsPath = Path.Combine(pluginConfigFolder, "settings.json");
        // 名单跟设置放一起。右键菜单里的「打开名单文件」直接把它交给记事本。
        _rosterPath = Path.Combine(pluginConfigFolder, "名单.txt");
    }

    /// <summary>当前设置。设置页直接绑这上面。</summary>
    public PickerSettings Settings => _settings;

    /// <summary>名单文件路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>插件配置目录。拍照要存原图时用。</summary>
    public string ConfigFolder => _configFolder;

    /// <summary>插件自己所在的目录。ONNX Runtime 和人脸模型都在这儿。</summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(PickerHostService).Assembly.Location) ?? string.Empty;

    /// <summary>把设置存盘。设置页改完调一下。</summary>
    public void SaveSettings() => SaveSettingsInternal();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = PickerSettings.Load(_settingsPath);
        _roster = new RosterService(_rosterPath);

        // 宿主启动 IHostedService 的时候 Avalonia 主窗口不一定已经就绪，
        // 用 Background 优先级排队，等 UI 空下来再开窗口。
        Dispatcher.UIThread.Post(ShowPickerWindow, DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SaveSettingsInternal();
        Dispatcher.UIThread.Post(() =>
        {
            RevealWindow.CloseCurrent();
            _window?.Close();
            _window = null;
        });
        _roster?.Dispose();
        // 摄像头可能还热着，一定要关掉，否则指示灯会一直亮。
        return CameraPicker.DisposeAllAsync();
    }

    private void ShowPickerWindow()
    {
        if (_window is not null || _roster is null)
        {
            return;
        }

        _window = new PickerWindow(_settings, _roster);
        _window.PickRequested += (_, _) => Pick();
        _window.SettingsChanged += (_, _) => SaveSettingsInternal();
        _window.HideRequested += (_, _) =>
        {
            RevealWindow.CloseCurrent();
            _window?.Hide();
            // 藏起来之后还能从「设置 → 提醒 → 随机抽选」那边知道插件还在，
            // 想叫回来重启 ClassIsland 就行。
        };
        _window.Show();
    }

    private void SaveSettingsInternal()
    {
        _window?.CapturePosition();
        _settings.Save(_settingsPath);
    }

    /// <summary>
    /// 抽一个人，然后按设置决定往哪儿显示。
    /// </summary>
    private void Pick()
    {
        if (_roster is null)
        {
            return;
        }

        if (_settings.Mode == PickMode.Photo)
        {
            PickFromPhoto();
            return;
        }

        PickFromRoster();
    }

    /// <summary>
    /// 拍照抽人：开摄像头、拍一张、检测、随机挑一个裁出来。
    /// </summary>
    /// <remarks>
    /// 整个过程要一两秒，全程在后台线程上跑，UI 只负责显示忙碌态。
    /// 检测不到人脸就退回按名单抽，并在卡片上说明原因——静默失败比抽错人更让人摸不着头脑。
    /// </remarks>
    private void PickFromPhoto()
    {
        if (_shooting)
        {
            return;
        }

        _shooting = true;
        _window?.SetBusy(true);

        _ = Task.Run(async () =>
        {
            var result = await CameraPicker.CaptureAndPickAsync(_settings, PluginDirectory, _configFolder);
            Dispatcher.UIThread.Post(() =>
            {
                _shooting = false;
                _window?.SetBusy(false);

                if (result is { Success: true, Portrait: not null })
                {
                    RevealWindow.Show(result.Portrait, null,
                        _settings.PortraitHeight,
                        TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
                        _window?.Accent ?? DefaultAccent);
                    return;
                }

                // 拍不到 / 认不出来就退回名单，但要把原因说清楚。
                PickFromRoster($"拍照失败:{result.Message}，已改为按名单抽取");
            });
        });
    }

    /// <summary>按名单抽一个。</summary>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    private void PickFromRoster(string? note = null)
    {
        if (_roster is null)
        {
            return;
        }

        var name = _roster.Pick(_settings);
        SaveSettingsInternal();
        _window?.RefreshCounter();

        if (name is null)
        {
            // 名单是空的——与其静悄悄什么都不发生，不如直接把话说清楚。
            Reveal(note is null ? "名单是空的" : note, isHint: true);
            return;
        }

        if (_settings.ShowCenterReveal)
        {
            Reveal(name, isHint: false, note);
        }

        if (_settings.ShowNotification)
        {
            SendNotification(name);
        }
    }

    private void Reveal(string text, bool isHint, string? note = null)
    {
        // 复用已经开着的那个窗口（内部会处理），连点也不会叠出一摞。
        RevealWindow.Show(
            text,
            note,
            isHint ? _settings.RevealFontSize * 0.42 : _settings.RevealFontSize,
            TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
            _window?.Accent ?? DefaultAccent);
    }

    private static readonly Avalonia.Media.Color DefaultAccent =
        Avalonia.Media.Color.FromRgb(0x5B, 0x8D, 0xEF);

    private void SendNotification(string name)
    {
        // 提供方是宿主用 AddHostedService 建的，这里按类型把那一份取回来。
        var provider = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<PickerNotificationProvider>()
            .FirstOrDefault();
        if (provider is null)
        {
            return;
        }

        // 连着抽人时，上一条还没播完就来了下一条，主界面上会排队堆积。
        // 直接把上一条取消掉——用户只关心最新抽到的那个人。
        _lastRequest?.Cancel();

        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask("随机抽选", hasRightIcon: false, factory: x =>
            {
                // 遮罩只是个引子，压得短一点，让名字尽快出来；连着抽时也不至于一直卡在遮罩上。
                x.Duration = TimeSpan.FromSeconds(0.9);
                x.IsSpeechEnabled = false;
            }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(name, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds + 2.0, 2.0, 30));
                x.IsSpeechEnabled = false;
            })
        };

        _lastRequest = request;
        provider.ShowNotification(request);
    }
}
