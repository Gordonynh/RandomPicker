using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Views;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 插件主体：管住悬浮窗、设置、名单，以及抽中之后往哪儿显示。
/// </summary>
/// <remarks>
/// 所有抽选入口（悬浮窗点击、URL、本地 HTTP、自动化行动）最后都汇到
/// <see cref="RequestPick"/>。它统一切回 UI 线程再执行——拍照回调在后台线程，
/// HTTP 在 socket 线程，只有 UI 线程能碰窗口和提醒。
/// </remarks>
public class PickerHostService : IHostedService
{
    /// <summary>一共提供几份名单。1 号沿用原来的 <c>名单.txt</c>，老配置升级后不会丢名单。</summary>
    public const int RosterSlots = 5;

    private readonly string _settingsPath;
    private readonly string _configFolder;
    private readonly PickerEventService _events;

    /// <summary>拍照抽人正在跑。摄像头开一次要一两秒，这期间再点就直接忽略。</summary>
    private bool _shooting;

    private PickerSettings _settings = new();
    private RosterService? _roster;
    private RosterService? _textRoster;
    private PickerWindow? _window;
    private PickerHttpApi? _http;

    /// <summary>上一条还在播的提醒。连着抽人时先把它取消掉，免得在主界面上排队堆积。</summary>
    private NotificationRequest? _lastRequest;

    /// <summary>上一次广播出去的悬浮窗可见状态，避免重复触发「当隐藏/显示时」。</summary>
    private bool _lastPublishedVisible = true;

    /// <summary>宿主的课程服务，用来判断现在是上课还是课间。可能拿不到（测试/老版本宿主）。</summary>
    private ILessonsService? _lessons;

    /// <summary>当前是否处于课间。由课程服务的状态事件实时更新。</summary>
    private volatile bool _onBreak;

    /// <summary>上一次抽选开始的时间戳（UTC）。打断点名关闭时用来判断上一次是否还在播。</summary>
    private DateTime _lastPickAt = DateTime.MinValue;

    public PickerHostService(string pluginConfigFolder, PickerEventService events)
    {
        _configFolder = pluginConfigFolder;
        _events = events;
        _settingsPath = Path.Combine(pluginConfigFolder, "settings.json");
    }

    /// <summary>当前设置。设置页直接绑这上面。</summary>
    public PickerSettings Settings => _settings;

    /// <summary>第 <paramref name="index"/> 份名单的文件名（1 号是老名字 <c>名单.txt</c>）。</summary>
    public static string RosterFileName(int index) =>
        index <= 1 ? "名单.txt" : $"名单{index}.txt";

    /// <summary>第 <paramref name="index"/> 份名单的完整路径。</summary>
    public string RosterPathFor(int index) =>
        Path.Combine(_configFolder, RosterFileName(Math.Clamp(index, 1, RosterSlots)));

    /// <summary>当前正在使用的名单文件路径。</summary>
    public string RosterPath => RosterPathFor(_settings.ActiveRosterIndex);

    /// <summary>菜单和设置页里显示的一行标签，顺带带上人数。</summary>
    public string RosterLabel(int index) =>
        $"名单 {index}（{RosterService.CountNames(RosterPathFor(index))} 人）";

    /// <summary>插件配置目录。拍照要存原图时用。</summary>
    public string ConfigFolder => _configFolder;

    /// <summary>主名单服务。设置页的权重编辑器读它。</summary>
    public RosterService? Roster => _roster;

    /// <summary>本地 HTTP 接口，设置页改端口/开关后要重启它。</summary>
    public PickerHttpApi? HttpApi => _http;

    /// <summary>本轮还剩多少人（小组模式下是还剩多少组）。</summary>
    public int RemainingInRound => _settings.Mode == PickMode.Group
        ? Math.Max(0, GroupService.GroupCount(_roster?.Names.Count ?? 0, _settings.GroupSize)
                      - (_settings.DrawnGroupsThisRound?.Count ?? 0))
        : _roster?.RemainingInRound(_settings) ?? 0;

    /// <summary>插件自己所在的目录。ONNX Runtime 和人脸模型都在这儿。</summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(PickerHostService).Assembly.Location) ?? string.Empty;

    /// <summary>把设置存盘。设置页改完调一下。</summary>
    public void SaveSettings() => SaveSettingsInternal();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = PickerSettings.Load(_settingsPath);
        _settings.ActiveRosterIndex = Math.Clamp(_settings.ActiveRosterIndex, 1, RosterSlots);
        // 用已存的隐藏状态初始化，避免启动瞬间误触发「当显示/隐藏时」。
        _lastPublishedVisible = !_settings.HideFloatingWindow;
        _roster = new RosterService(RosterPath);
        _roster.RosterChanged += OnRosterFileChanged;

        // 自动化行动和 URL 都通过事件服务请求抽选，把执行器挂上。
        _events.PickExecutor = RequestPick;

        _http = new PickerHttpApi(_events, this);
        _http.Restart();

        // 挂上课程服务：课间禁用要实时知道现在是上课还是课间。
        // ILessonsService 取不到就不挂——闸门永远放行，等于功能没开。
        _lessons = IAppHost.Host?.Services.GetService<ILessonsService>();
        if (_lessons is not null)
        {
            _lessons.OnClass += MarkNotBreaking;
            _lessons.OnAfterSchool += MarkNotBreaking;
            _lessons.OnBreakingTime += MarkBreaking;
            _lessons.CurrentTimeStateChanged += OnTimeStateChanged;
            _onBreak = _lessons.CurrentState == TimeState.Breaking;
        }

        // 宿主启动 IHostedService 的时候 Avalonia 主窗口不一定已经就绪，
        // 用 Background 优先级排队，等 UI 空下来再开窗口。
        // 记住上次的隐藏状态：藏起来了就不建窗口，而不是建完再藏（会闪一下）。
        if (!_settings.HideFloatingWindow)
        {
            Dispatcher.UIThread.Post(ShowPickerWindow, DispatcherPriority.Background);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _events.PickExecutor = null;
        _http?.Dispose();
        _http = null;

        SaveSettingsInternal();
        Dispatcher.UIThread.Post(() =>
        {
            RevealWindow.CloseCurrent();
            _window?.Close();
            _window = null;
        });

        if (_roster is not null)
        {
            _roster.RosterChanged -= OnRosterFileChanged;
        }

        _roster?.Dispose();
        _textRoster?.Dispose();

        if (_lessons is not null)
        {
            _lessons.OnClass -= MarkNotBreaking;
            _lessons.OnAfterSchool -= MarkNotBreaking;
            _lessons.OnBreakingTime -= MarkBreaking;
            _lessons.CurrentTimeStateChanged -= OnTimeStateChanged;
            _lessons = null;
        }
        // 摄像头可能还热着，一定要关掉，否则指示灯会一直亮。
        return CameraPicker.DisposeAllAsync();
    }

    private void OnRosterFileChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(OnRosterChanged);

    private void OnRosterChanged()
    {
        // 权重是按名字全局存的，多份名单共用一本字典。切到名单 2 时不能把
        // 只在名单 1 里出现过的权重删掉，所以清理范围取所有名单的并集。
        var valid = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i <= RosterSlots; i++)
        {
            foreach (var name in RosterService.ReadNames(RosterPathFor(i)))
            {
                valid.Add(name);
            }
        }

        foreach (var name in _textRoster?.Names ?? Array.Empty<string>())
        {
            valid.Add(name);
        }

        _settings.PruneWeights(valid);
        _window?.RefreshCounter();
        SaveSettingsInternal();
    }

    #region 悬浮窗与名单

    private void ShowPickerWindow()
    {
        if (_roster is null)
        {
            return;
        }

        // 已经建过窗口（只是被 Hide 了）就直接叫回来，趁便恢复置顶重申。
        if (_window is not null)
        {
            if (_window.IsHidden)
            {
                _window.SetHidden(false);
            }

            return;
        }

        _window = new PickerWindow(_settings, _roster, this);
        _window.PickRequested += (_, _) => RequestPick(PickRequest.FromWindow());
        _window.SettingsChanged += (_, _) => SaveSettingsInternal();
        _window.HideRequested += (_, _) => SetFloatingWindowVisible(false);
        _window.Show();
    }

    /// <summary>
    /// 显示 / 隐藏悬浮窗，并把状态写进设置，重启后仍然记得。
    /// </summary>
    /// <remarks>
    /// 以前隐藏只调 <c>Hide()</c>，还会被置顶定时器带 <c>SWP_SHOWWINDOW</c> 拉回来；
    /// 现在 <see cref="PickerWindow.SetHidden"/> 会同时挂起定时器，设置页也能随时叫回来，
    /// 不用再重启 ClassIsland。
    /// </remarks>
    public void SetFloatingWindowVisible(bool visible)
    {
        _settings.HideFloatingWindow = !visible;
        SaveSettingsInternal();

        if (visible)
        {
            Dispatcher.UIThread.Post(ShowPickerWindow, DispatcherPriority.Background);
        }
        else
        {
            RevealWindow.CloseCurrent();
            _window?.SetHidden(true);
        }

        // 广播状态变化，自动化「当隐藏时 / 当显示时」触发器据此触发。
        PublishVisibility(visible);
    }

    /// <summary>切换悬浮窗显隐，全局快捷键用。当前隐藏就显示，当前显示就隐藏。</summary>
    public void ToggleFloatingWindowVisible() => SetFloatingWindowVisible(_settings.HideFloatingWindow);

    /// <summary>悬浮窗当前是否可见（未隐藏）。</summary>
    public bool IsFloatingWindowVisible => !_settings.HideFloatingWindow;

    private void PublishVisibility(bool visible)
    {
        if (_lastPublishedVisible == visible)
        {
            return;
        }

        _lastPublishedVisible = visible;
        _events.PublishFloatingWindowVisibility(visible);
    }

    /// <summary>设置页改了快捷键后，让快捷键服务重新注册（找不到服务就静默忽略）。</summary>
    public void ApplyHotkey()
    {
        var svc = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<PickerHotkeyService>()
            .FirstOrDefault();
        svc?.ApplyHotkey();
    }

    /// <summary>
    /// 切换到第 <paramref name="index"/> 份名单。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="RosterService.SwitchTo"/> 换路径而不是换实例，悬浮窗持有的名单引用
    /// 一直有效；切换后自动触发一次名单变更，权重清理和圆钮计数都会跟着刷新。
    /// </remarks>
    public void SwitchRoster(int index)
    {
        if (_roster is null)
        {
            return;
        }

        index = Math.Clamp(index, 1, RosterSlots);
        _settings.ActiveRosterIndex = index;
        _roster.SwitchTo(RosterPathFor(index));
        SaveSettingsInternal();
        _window?.RefreshCounter();
    }

    /// <summary>重新读名单（设置页「刷新名单」按钮）。</summary>
    public void ReloadRoster()
    {
        _roster?.Reload();
        _textRoster?.Reload();
        OnRosterChanged();
    }

    /// <summary>设置页改了 HTTP 端口/开关之后调一下。</summary>
    public void RestartHttpApi() => _http?.Restart();

    /// <summary>设置页改了悬浮窗缩放系数之后调一下，让圆钮按新直径重排。</summary>
    public void ApplyFloatingScale() => Dispatcher.UIThread.Post(() => _window?.ApplySize());

    #region 课间状态

    private void MarkNotBreaking(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _onBreak = false);

    private void MarkBreaking(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _onBreak = true);

    /// <summary>兜底：不管状态变成了什么，直接按当前状态重算，避免漏接事件后状态卡死。</summary>
    private void OnTimeStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
            _onBreak = _lessons is not null && _lessons.CurrentState == TimeState.Breaking);

    #endregion

    #endregion

    private void SaveSettingsInternal()
    {
        _window?.CapturePosition();
        _settings.Save(_settingsPath);
    }

    #region 抽选入口

    /// <summary>
    /// 请求抽一次。可以从任何线程调用，内部统一切回 UI 线程。
    /// </summary>
    public void RequestPick(PickRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ExecutePick(request);
            return;
        }

        Dispatcher.UIThread.Post(() => ExecutePick(request));
    }

    private void ExecutePick(PickRequest request)
    {
        if (_roster is null)
        {
            return;
        }

        // 课间禁用：所有入口（悬浮窗 / URL / HTTP / 自动化）都汇到这里，一处闸门全拦。
        if (_settings.DisableDuringBreak && _onBreak)
        {
            _window?.FlashMessage("课间禁用");
            return;
        }

        // 打断点名：开着时新抽选立刻关掉上一条还没播完的中央大字
        //（上一条提醒在 SendNotification 里本来就取消了）；关着时上一次点名
        // 还在播（约等于大字停留时长内）就忽略新的请求，不去打断它。
        var sinceLast = (DateTime.UtcNow - _lastPickAt).TotalSeconds;
        if (!_settings.InterruptPreviousPick && sinceLast < Math.Max(0.5, _settings.RevealSeconds))
        {
            return;
        }

        if (_settings.InterruptPreviousPick)
        {
            RevealWindow.CloseCurrent();
        }

        _lastPickAt = DateTime.UtcNow;

        // 外部明确要小组时，压过插件当前的日常模式。
        if (request.Kind == PickKind.Group || request.Mode == PickMode.Group)
        {
            PickFromGroup(request);
            return;
        }

        var kind = request.Kind == PickKind.Auto
            ? _settings.Mode switch
            {
                PickMode.Photo => PickKind.Photo,
                PickMode.Group => PickKind.Group,
                _ => PickKind.Roster
            }
            : request.Kind;

        if (kind == PickKind.Group)
        {
            PickFromGroup(request);
            return;
        }

        if (kind == PickKind.Photo)
        {
            // 按设定的概率改成抽名字。掷骰子用密码学随机数，
            // 和抽人用的是同一个源，不会出现「每次开机前几抽都一样」。
            // 外部明确指定拍照时（HonorPhotoTextChance=false）不做这件事。
            if (request.HonorPhotoTextChance && RollTextChance())
            {
                PickFromRoster(request);
                return;
            }

            PickFromPhoto(request);
            return;
        }

        PickFromRoster(request);
    }

    private bool RollTextChance()
    {
        var chance = Math.Clamp(_settings.PhotoTextChance, 0, 100);
        return chance > 0 && RandomNumberGenerator.GetInt32(100) < chance;
    }

    /// <summary>
    /// 抽一个小组：按设置里的「每组人数」把当前名单切组，从本轮没抽过的组里挑一组，
    /// 大字显示组名和组员。组有独立的轮次记录，不占按人抽选的「本轮已抽」。
    /// </summary>
    private void PickFromGroup(PickRequest request)
    {
        if (_roster is null)
        {
            return;
        }

        var groups = GroupService.Build(_roster.Names, _settings.GroupSize);
        var reveal = request.ShowCenterReveal ?? _settings.ShowCenterReveal;
        var notify = request.ShowNotification ?? _settings.ShowNotification;

        if (groups.Count == 0)
        {
            if (reveal)
            {
                Reveal("名单是空的", isHint: true);
            }

            return;
        }

        var group = GroupService.Pick(groups, _settings);
        SaveSettingsInternal();
        _window?.RefreshCounter();

        var members = string.Join("、", group.Members);
        // 组名一行、组员一行。中央大字会逐行独立居中，行宽不同也不会歪。
        var lines = group.Label + "\n" + members;

        if (reveal)
        {
            Reveal(lines, isHint: false, null, 0.5);
        }

        if (notify)
        {
            SendNotification(group.Label + "：" + members);
        }

        _events.Publish(new PickEventArgs
        {
            Names = group.Members,
            DisplayText = lines,
            Source = PickSource.Group,
            Mode = PickMode.Group,
            Count = 1,
            Origin = request.Origin
        });
    }

    /// <summary>
    /// 按名单抽（支持一次抽多人、加权、以及 URL 指定的名单）。
    /// </summary>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    private void PickFromRoster(PickRequest request, string? note = null)
    {
        if (_roster is null)
        {
            return;
        }

        var roster = ChooseRoster(request);
        var count = request.NormalizedCount(Math.Max(1, roster.Names.Count));
        var reveal = request.ShowCenterReveal ?? _settings.ShowCenterReveal;
        var notify = request.ShowNotification ?? _settings.ShowNotification;

        // 外部可以临时指定「随机 / 不重复」，抽完就还原，不改动用户的日常模式。
        var originalMode = _settings.Mode;
        if (request.Mode is { } overrideMode)
        {
            _settings.Mode = overrideMode;
        }

        List<string> names;
        try
        {
            names = roster.PickMany(_settings, count);
        }
        finally
        {
            _settings.Mode = originalMode;
        }

        SaveSettingsInternal();
        _window?.RefreshCounter();

        if (names.Count == 0)
        {
            // 名单是空的——与其静悄悄什么都不发生，不如直接把话说清楚。
            if (reveal)
            {
                Reveal(note ?? "名单是空的", isHint: true);
            }

            return;
        }

        var text = string.Join("、", names);
        if (reveal)
        {
            // 一次抽多人时名字会很长，字小一点才放得下。
            Reveal(text, isHint: false, note, names.Count > 1 ? 0.66 : 1.0);
        }

        if (notify)
        {
            SendNotification(text);
        }

        _events.Publish(new PickEventArgs
        {
            Names = names,
            DisplayText = text,
            Source = ReferenceEquals(roster, _roster) ? PickSource.Roster : PickSource.Text,
            Mode = request.Mode ?? originalMode,
            Count = names.Count,
            Note = note,
            Origin = request.Origin
        });
    }

    /// <summary>
    /// 拍照抽人：开摄像头、拍一张、检测、随机挑一个或多个裁出来。
    /// </summary>
    /// <remarks>
    /// 整个过程要一两秒，全程在后台线程上跑，UI 只负责显示忙碌态。
    /// 检测不到人脸就退回按名单抽，并在卡片上说明原因——静默失败比抽错人更让人摸不着头脑。
    /// </remarks>
    private void PickFromPhoto(PickRequest request)
    {
        if (_shooting)
        {
            return;
        }

        _shooting = true;
        _window?.SetBusy(true);

        var count = Math.Clamp(request.Count <= 0 ? 1 : request.Count, 1, 12);
        var reveal = request.ShowCenterReveal ?? _settings.ShowCenterReveal;
        var notify = request.ShowNotification ?? _settings.ShowNotification;

        _ = Task.Run(async () =>
        {
            var result = await CameraPicker.CaptureAndPickAsync(
                _settings, PluginDirectory, _configFolder, count);
            Dispatcher.UIThread.Post(() =>
            {
                _shooting = false;
                _window?.SetBusy(false);

                if (result.Success && result.Portraits.Count > 0)
                {
                    if (reveal)
                    {
                        RevealWindow.Show(result.Portraits, null,
                            _settings.PortraitHeight,
                            TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
                            _window?.Accent ?? DefaultAccent);
                    }

                    if (notify)
                    {
                        SendNotification(result.Message);
                    }

                    _events.Publish(new PickEventArgs
                    {
                        Names = Array.Empty<string>(),
                        DisplayText = result.Message,
                        Source = PickSource.Photo,
                        Mode = PickMode.Photo,
                        Count = result.Portraits.Count,
                        Origin = request.Origin
                    });
                    return;
                }

                // 拍不到 / 认不出来就退回名单，但要把原因说清楚。
                PickFromRoster(request, $"拍照失败:{result.Message}，已改为按名单抽取");
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

    private RosterService ChooseRoster(PickRequest request) => request.Roster switch
    {
        PickRosterSource.Main => _roster!,
        PickRosterSource.Text => TextRoster,
        _ => TextRoster
    };

    #endregion

    #region 展示

    private void Reveal(string text, bool isHint, string? note = null, double sizeScale = 1.0)
    {
        // 复用已经开着的那个窗口（内部会处理），连点也不会叠出一摞。
        RevealWindow.Show(
            text,
            note,
            _settings.RevealFontSize * (isHint ? 0.42 : 1.0) * sizeScale,
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

    #endregion
}
