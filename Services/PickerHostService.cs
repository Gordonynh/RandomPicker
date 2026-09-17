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

    /// <summary>抽选动画正在播。这期间再点也直接忽略——不然结果会被后一次抽选顶掉。</summary>
    private bool _animating;

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

    /// <summary>
    /// 设置被改动并落盘之后触发。
    /// </summary>
    /// <remarks>
    /// 右键菜单和设置页改的是<b>同一份</b>设置，但两边都是「谁改谁负责刷新自己」——
    /// 于是从菜单改完动画样式，已经打开着的设置页不会知道，下拉框还停在旧的一档上，
    /// 看起来就是「两处选择不一致」。这条事件用来把改动广播出去。
    /// </remarks>
    public event EventHandler? SettingsSaved;

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
            // 插件要停了，正在播的动画会被掐掉——同一个理由，标记得手动清。
            ReleaseAnimation();
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
        // 动画播到一半时点钮，含义是「跳过动画」，而不是「再抽一次」。
        _window.SkipRequested += (_, _) => RevealWindow.SkipAnimation();
        _window.SettingsChanged += (_, _) => SaveSettingsInternal();
        _window.HideRequested += (_, _) =>
        {
            // 藏起来的时候正在播的动画会被掐掉，而掐掉的动画不会再回调收尾，
            // 所以「正在播」这个标记必须在这里手动清掉——不清的话以后再也抽不动了。
            ReleaseAnimation();
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
        // 广播出去，让设置页把自己刷成最新值。
        // 悬浮钮右键菜单和设置页是同一份设置的两个入口，两边显示必须一致。
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 抽一个人，然后按设置决定往哪儿显示。
    /// </summary>
    private void Pick()
    {
        // 上一轮还在演的时候再点没有意义：结果已经定好了，硬插进去只会让画面和中选者对不上。
        if (_animating)
        {
            return;
        }

        if (_roster is null)
        {
            return;
        }

        if (_settings.Mode == PickMode.Photo)
        {
            // 按设定的概率改成抽名字。掷骰子用密码学随机数，
            // 和抽人用的是同一个源，不会出现「每次开机前几抽都一样」。
            var chance = Math.Clamp(_settings.PhotoTextChance, 0, 100);
            if (chance > 0 && System.Security.Cryptography.RandomNumberGenerator.GetInt32(100) < chance)
            {
                PickFromRoster();
                return;
            }

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
        _window?.SetBusy(PickerBusyKind.Shooting);

        _ = Task.Run(async () =>
        {
            var result = await CameraPicker.CaptureAndPickAsync(_settings, PluginDirectory, _configFolder);
            Dispatcher.UIThread.Post(() =>
            {
                _shooting = false;
                _window?.SetBusy(PickerBusyKind.None);

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

    /// <summary>
    /// 文字抽选用的名单。
    /// </summary>
    /// <remarks>
    /// 开了「文字抽选用单独名单」就读那一份，否则还是主名单。
    /// 两份各自有各自的「本轮已抽」状态吗？——没有，共用一套。
    /// 名单隔离是为了圈定范围，不是为了各记各的进度。
    /// </remarks>
    private RosterService TextRoster
    {
        get
        {
            if (!_settings.SeparateTextRoster)
            {
                return _roster!;
            }

            _textRoster ??= new RosterService(
                Path.Combine(_configFolder, "名单-文字.txt"));

            return _textRoster;
        }
    }

    private RosterService? _textRoster;

    /// <summary>按名单抽一个。</summary>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    private void PickFromRoster(string? note = null)
    {
        // 同上：动画没播完就再来一发，只会让画面和中选者对不上。
        if (_animating || _roster is null)
        {
            return;
        }

        // 结果在这一刻就抽好了。往后播不播动画、播哪一种，都不会再改变他。
        var name = TextRoster.Pick(_settings);
        SaveSettingsInternal();

        if (name is null)
        {
            // 名单是空的——与其静悄悄什么都不发生，不如直接把话说清楚。
            // 这一下没抽成，所以钮上的剩余人数现在就刷新。
            _window?.RefreshCounter();
            Reveal(note is null ? "名单是空的" : note, isHint: true);
            return;
        }

        StartAnimation(name, note);
    }

    /// <summary>
    /// 解开「正在播动画」的封锁，并把悬浮钮恢复成剩余人数。
    /// </summary>
    /// <remarks>
    /// 只在动画被<b>中途掐掉</b>的地方调用（藏窗口、插件停止）。
    /// 正常播完的那条路走的是 <see cref="StartAnimation"/> 里的回调，那边自己会解。
    /// </remarks>
    private void ReleaseAnimation()
    {
        _animating = false;
        _window?.SetBusy(PickerBusyKind.None);
    }

    /// <summary>
    /// 该播动画就播，播完再出结果；不该播就直接出结果。
    /// </summary>
    /// <param name="name">中选者。</param>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    /// <remarks>
    /// <b>动画完全不参与抽选</b>：名字在这里之前就已经定好了，这段代码只决定「怎么演」。
    /// 所以任何一步出问题（名单数据不满足这个样式、时长配置被改坏），
    /// 都可以放心地退化成「直接出结果」，不会影响公平性。
    /// </remarks>
    private void StartAnimation(string name, string? note)
    {
        // 只认插件自己的设置：宿主的「动画级别」不参与判断。
        // 2026-09-15 主人定的——动画是这个功能本身，不是界面装饰，
        // ClassIsland 关掉自己的界面过渡不该连累抽选表演。
        // 想彻底关掉，在右键菜单里选「无动画」；那条路下面会被 Build 判成 null。
        var style = _settings.AnimationStyle;

        // 按样式造排片表；数据不满足要求时返回 null（比如名单里有超过四个字的名字却选了老虎机）。
        var plan = RevealAnimationPlanner.Build(style, TextRoster.Names, name, _settings);

        // 不播动画这条路：直接出结果。
        if (plan is null)
        {
            // 没有动画就没什么可等的，出结果的同时把钮上的剩余人数刷成新的。
            _window?.RefreshCounter();
            FinishReveal(name, note);
            return;
        }

        // 播动画这条路：这期间挡住新的抽选，钮上显示「跳」，点一下可以跳过。
        _animating = true;
        _window?.SetBusy(PickerBusyKind.Animating);

        // 停留时长从一开始就传进去，动画播完它才开始计时。
        var hold = TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30));

        RevealWindow.Play(plan, _settings.RevealFontSize, _window?.Accent ?? DefaultAccent, hold, () =>
        {
            // 动画播完（或被跳过）的这一刻才是「结果揭晓」：
            // 先把钮上的状态收掉——SetBusy(None) 会顺手把剩余人数刷成新的，
            // 而那正是这一抽的结果之一，所以必须在动画结束之后才刷，否则等于提前剧透。
            _animating = false;
            _window?.SetBusy(PickerBusyKind.None);
            // 中央大字和 ClassIsland 提醒都在这里才发出去，免得提醒先于动画到达。
            FinishReveal(name, note);
        });
    }

    /// <summary>
    /// 把中选者亮出来：中央大字 + ClassIsland 提醒。
    /// </summary>
    /// <param name="name">中选者的名字。</param>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    /// <remarks>
    /// 单独拆成一个方法，是为了让「动画播完之后」和「不播动画直接出结果」这两条路
    /// 走一模一样的收尾逻辑——免得两边各写一遍，将来改一处忘一处。
    /// </remarks>
    private void FinishReveal(string name, string? note)
    {
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
