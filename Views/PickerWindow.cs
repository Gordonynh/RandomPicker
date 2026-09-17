using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Interop;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 抽人用的小悬浮窗：一个圆钮，按一下抽一个，右键（或触摸长按）出设置菜单。
/// </summary>
/// <remarks>
/// 交互上要同时容纳三件事——点击抽人、拖动挪窝、唤出菜单：
/// <list type="bullet">
/// <item>按下后没怎么动就松开 → 抽人</item>
/// <item>移动超过阈值 → 转成拖窗口，松手时不触发抽选</item>
/// <item>右键，或者触摸按住不动超过 <see cref="HoldMilliseconds"/> → 菜单</item>
/// </list>
/// 拖动范围夹在当前屏幕内，可以压在任务栏上，但拖不出屏幕。
/// </remarks>
public class PickerWindow : Window
{
    /// <summary>按下后移动超过这么多逻辑像素，就认为用户是想拖窗口而不是点按钮。</summary>
    private const double DragThreshold = 4.0;

    /// <summary>触摸按住多久算长按。参照系统右键长按的手感。</summary>
    private const int HoldMilliseconds = 450;

    private readonly PickerSettings _settings;
    private readonly RosterService _roster;
    private readonly Border _knob;
    private readonly TextBlock _label;
    private readonly TextBlock _counter;
    private readonly DispatcherTimer _holdTimer;

    /// <summary>设置菜单。复用同一个实例，每次打开前换 ItemsSource。</summary>
    private MenuFlyout? _menu;

    /// <summary>当前被本窗口捕获的指针。长按弹菜单前要把它放掉。</summary>
    private IPointer? _capturedPointer;

    private TopmostEnforcer? _topmost;
    private bool _pointerDown;
    private bool _dragging;
    private bool _menuOpened;
    private Point _pressOrigin;
    private PixelPoint _grabOffset;

    public PickerWindow(PickerSettings settings, RosterService roster)
    {
        _settings = settings;
        _roster = roster;

        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Manual;

        _label = new TextBlock
        {
            Text = "抽",
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _counter = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.White, 0.62),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        // 深色玻璃底 + 一条主题色细边。整个圆钮只有一个强调色，其余全是中性色——
        // 它要在桌面上常驻，越安静越好。
        _knob = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x1A, 0x1A, 0x20)),
            BorderBrush = new SolidColorBrush(Colors.White, 0.22),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _label, _counter }
            },
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                OffsetX = 0,
                OffsetY = 2,
                Color = Colors.Black,
                Opacity = 0.4
            },
            Cursor = new Cursor(StandardCursorType.Hand),
            RenderTransform = TransformOperations.Parse("scale(1)"),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(110),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            ]
        };

        Content = _knob;
        ApplyAccent();
        ApplySize();
        RefreshCounter();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
        _holdTimer.Tick += OnHoldElapsed;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerEntered += (_, _) => _knob.Opacity = 0.92;
        PointerExited += (_, _) => _knob.Opacity = 1.0;
        PositionChanged += (_, _) => ClampToScreen();

        _roster.RosterChanged += (_, _) => Dispatcher.UIThread.Post(RefreshCounter);
    }

    /// <summary>用户请求抽一个人。</summary>
    public event EventHandler? PickRequested;

    /// <summary>用户请求跳过正在播的抽选动画。</summary>
    /// <remarks>只在钮处于「跳」的状态时才会触发；跳过只是不看表演，不改变谁被抽中。</remarks>
    public event EventHandler? SkipRequested;

    /// <summary>设置被菜单改动，需要持久化。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>用户要求隐藏悬浮窗。</summary>
    public event EventHandler? HideRequested;

    #region 外观

    /// <summary>当前主题色，取自应用已生成的 <c>SystemAccentColor</c>。</summary>
    public Color Accent { get; private set; } = Color.FromRgb(0x5B, 0x8D, 0xEF);

    /// <summary>跟随应用主题色刷新描边。</summary>
    public void ApplyAccent()
    {
        if (Application.Current is { } app &&
            app.TryFindResource("SystemAccentColor", app.ActualThemeVariant, out var value))
        {
            Accent = value switch
            {
                Color c => c,
                SolidColorBrush b => b.Color,
                _ => Accent
            };
        }

        _knob.BorderBrush = new SolidColorBrush(Accent, 0.75);
    }

    /// <summary>按当前尺寸档位刷新窗口与圆钮的大小。</summary>
    public void ApplySize()
    {
        var d = _settings.Diameter;
        Width = d;
        Height = d;
        _knob.Width = d;
        _knob.Height = d;
        _knob.CornerRadius = new CornerRadius(d / 2);
        _label.FontSize = d * 0.30;
        _counter.FontSize = d * 0.155;
        ClampToScreen();
    }

    /// <summary>刷新圆钮上的剩余人数。</summary>
    public void RefreshCounter()
    {
        // 忙碌期间钮上显示的是状态而不是人数，别把它刷掉。
        if (_busy != PickerBusyKind.None)
        {
            return;
        }

        _label.Text = "抽";

        if (_settings.Mode == PickMode.Photo)
        {
            // 拍照模式和名单没关系，显示人数只会误导。
            _counter.Text = "拍照";
            return;
        }

        var total = _roster.Names.Count;
        if (total == 0)
        {
            _counter.Text = "空";
            return;
        }

        // 不重复模式显示「本轮还剩几个」，纯随机模式没有轮次概念，显示总人数。
        _counter.Text = _settings.Mode == PickMode.NoRepeat
            ? $"{_roster.RemainingInRound(_settings)}/{total}"
            : total.ToString();
    }

    /// <summary>
    /// 悬浮钮的忙碌态。
    /// </summary>
    /// <remarks>
    /// 拍照要开摄像头、跑检测，抽选动画要播好几秒，这两种情况钮上都必须有反馈，
    /// 否则用户会以为没点上而反复戳。
    /// </remarks>
    public void SetBusy(PickerBusyKind kind)
    {
        _busy = kind;
        switch (kind)
        {
            case PickerBusyKind.Shooting:
                // 拍照途中：换成相机图标，说明正在拍。
                _label.Text = "📷";
                _counter.Text = "拍摄中";
                return;

            case PickerBusyKind.Animating:
                // 动画途中：这一下点击不再是「再抽一次」，而是「跳过动画」。
                // 钮上直接写「跳」，用户一眼就知道点下去能跳过，不用等它慢慢播完。
                _label.Text = "跳";
                _counter.Text = "跳过";
                return;

            default:
                // 忙完了：把「抽」和剩余人数刷回来（这个时机正好是结果出现的时候）。
                RefreshCounter();
                return;
        }
    }

    private PickerBusyKind _busy;

    #endregion

    #region 位置

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 先按设置摆一次。屏幕信息还没就绪时会返回 false，由下面的重试兜住。
        RestorePosition();
        ClampToScreen();
        ApplyAccent();

        // 喵~防御：刚开机时显示器信息可能迟到——那一刻拿不到屏幕、拿到的是空矩形，
        // 或者拿到的是个<b>非空但尺寸不对</b>的占位矩形。这时候摆位和夹取都不算数，
        // 窗口就停在系统给的默认位置，也就是左上角 (0,0)。
        // 这就是「重启后悬浮钮跑到左上角」那个 bug。这里每 250 毫秒重试一次，
        // 最多试 5 秒；一旦摆成功就立刻停掉，不打扰后面的正常使用。
        StartPositionRetry();

        _topmost = new TopmostEnforcer(this);
        _topmost.Attach();
    }

    /// <summary>摆位没成功时的重试计时器。</summary>
    private DispatcherTimer? _positionRetryTimer;

    /// <summary>已经重试了几次。用来兜住「屏幕信息一直没就绪」这种极端情况。</summary>
    private int _positionRetryCount;

    /// <summary>最多重试几次。250 毫秒 × 60 次 = 15 秒。</summary>
    /// <remarks>
    /// 原来是 5 秒。开机那一段屏幕信息迟到得比这久的时候，重试就用完了，
    /// 而用完之后夹取会恢复正常、位置也就定死在原地——那是「跑到左上角」的另一半成因。
    /// 放宽到 15 秒：等待期间位置其实已经摆对了（见 <see cref="RestorePosition"/> 的顺序），
    /// 所以多试一会儿既不会打扰主人，又能把分辨率判定做完。
    /// </remarks>
    private const int MaxPositionRetries = 60;

    /// <summary>
    /// 摆位阶段是不是已经结束（摆好了，或者重试次数用完了）。
    /// </summary>
    /// <remarks>
    /// 只管一件事：<see cref="ClampToScreen"/> 在这之前一律不干活。
    /// 那段时间屏幕信息可能不可信，拿它夹取会把还没摆好的位置钉到假屏幕上去。
    /// <para/>
    /// <b>注意它和 <see cref="_positionTrustworthy"/> 是两回事，别合并。</b>
    /// 重试放弃时这个标记也会立起来（好让夹取恢复正常），但那时候的位置（0,0）
    /// 依然是不可信的，不能存盘。
    /// </remarks>
    private bool _placementSettled;

    /// <summary>
    /// 当前这个位置能不能信——能不能把它存回配置。
    /// </summary>
    /// <remarks>
    /// 只有两种情况算数：自动摆位确实摆成功了，或者主人自己拖动过。
    /// 其余时候（窗口还停在系统给的 (0,0) 上）记下来的都是坏值。
    /// 详见 <see cref="CapturePosition"/> 的说明。
    /// </remarks>
    private bool _positionTrustworthy;

    /// <summary>
    /// 分辨率「对不上」时要先重试几次，才认定它真的变了。
    /// </summary>
    /// <remarks>
    /// 开机那几秒显示器可能还没枚举完，<c>Screens.All</c> 里只有一块尺寸不对的占位屏幕。
    /// 那时候「记位置时的那块屏幕找不到了」和「分辨率真的变了」长得一模一样。
    /// 前者会自己好，所以先给它一点时间：8 × 250 毫秒 = 2 秒，开机那一段足够过去。
    /// 真是分辨率变了的话，多等这 2 秒也无所谓——那件事一辈子遇不上几次。
    /// </remarks>
    private const int ResolutionRetryCount = 8;

    /// <summary>
    /// 按设置把窗口摆到该在的地方。
    /// </summary>
    /// <returns>这一次把该做的都做完了返回 true；还要等屏幕信息、请稍后再试，返回 false。</returns>
    /// <remarks>
    /// <b>顺序是「先摆位置，再判分辨率」</b>，这个顺序很要紧：
    /// 记下来的坐标是上次存下来的正确值，先把它摆上去，主人立刻看到的就是对的位置，
    /// 不必等屏幕信息就绪。之后再拿屏幕信息判断「记位置时那块屏幕还在不在」，
    /// 判出分辨率真变过了才改摆默认角落。
    /// <para/>
    /// 反过来的话（先等屏幕信息再摆）在开机那几秒是有代价的：屏幕信息迟到多久，
    /// 窗口就停在 (0,0) 多久。2026-09-16 主人报的「开机后悬浮钮跑到左上角」正是这个，
    /// 而且更糟——见 <see cref="CapturePosition"/> 的说明，那时候还会把 (0,0) 存进配置。
    /// <para/>
    /// <b>这个方法是幂等的</b>，重试时重复调用不会出问题——所以能直接拿来当重试体。
    /// </remarks>
    private bool RestorePosition()
    {
        var screen = PickScreen();

        // 屏幕信息可信不可信，全交给纯计算层判（那边有单测钉着）。
        // 拿不到屏幕时一律按不可信处理。
        var screenUsable = screen is not null
                           && WindowPlacement.IsUsable(ToArea(screen.Bounds), screen.Scaling);

        // 有没有记过位置。
        var hasSavedPosition = _settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue;

        // 没记过位置（首次运行）：得有可信的屏幕信息才能算出默认角落在哪儿。
        if (!hasSavedPosition)
        {
            // 喵~防御：屏幕信息还没就绪时报 false 交给重试，绝不拿坏值算位置——
            // 拿空矩形算出来的「默认角落」就是屏幕原点，那正是左上角那个 bug。
            if (!screenUsable)
            {
                return false;
            }

            return PlaceAtDefaultCorner(ToArea(screen!.WorkingArea), screen.Scaling);
        }

        // 记过位置：先摆上去。这组坐标是上次存下来的正确值，先摆上去位置立刻就是对的。
        Position = new PixelPoint(_settings.WindowX, _settings.WindowY);

        // 喵~防御：屏幕信息还不可信时到此为止，但<b>不标记摆位结束</b>——
        // 继续留在重试循环里，等屏幕信息就绪了再判下面那条。
        // 这段等待期里位置已经是对的，主人看不出任何异常。
        if (!screenUsable)
        {
            return false;
        }

        // 喵~防御：坐标正好是 (0,0)、又从来没记过屏幕尺寸——这是老版本留下的脏数据。
        // 那时候「位置还没摆好就被存盘」那个 bug 还在，窗口停在系统默认的 (0,0) 时
        // 一存盘就把主人原来摆的位置覆盖掉了。这种数据不用等也不用猜，直接自愈回默认角落。
        // （主人当真把悬浮钮摆在左上角时，用新版本存过一次就会有屏幕尺寸记录，
        //  这一条不会再触发，不会误伤。）
        if (_settings.WindowX == 0 && _settings.WindowY == 0
            && _settings.WindowScreenWidth == int.MinValue)
        {
            return PlaceAtDefaultCorner(ToArea(screen!.WorkingArea), screen.Scaling);
        }

        // 屏幕信息可信了：判一判「记位置时那块屏幕还在不在」。
        var savedScreenGone = !WindowPlacement.MatchesAnyScreen(
            _settings.WindowScreenWidth, _settings.WindowScreenHeight, CurrentScreenAreas());

        if (savedScreenGone)
        {
            // 对不上，但还可能是「显示器还没枚举完」——那种会自己好，先重试几次再认账。
            // 真的是分辨率变了的话，多等这几秒也无所谓，那件事一辈子遇不上几次。
            if (_positionRetryCount < ResolutionRetryCount)
            {
                return false;
            }

            // 试够了还是对不上：分辨率确实变过了（换显示器、改分辨率、接投影），
            // 那组坐标在新屏幕上未必还有意义——回默认角落。
            return PlaceAtDefaultCorner(ToArea(screen!.WorkingArea), screen.Scaling);
        }

        // 位置对、屏幕也对：摆位阶段到此结束，之后 ClampToScreen 才恢复干活。
        _placementSettled = true;
        // 位置是排片表里存着的那一个，可信，可以存回配置。
        _positionTrustworthy = true;
        return true;
    }

    /// <summary>把窗口摆到主屏右下角靠里一点的位置。</summary>
    /// <param name="workingArea">主屏的工作区（不含任务栏）。</param>
    /// <param name="scaling">屏幕缩放比例。</param>
    /// <returns>摆好了返回 true；还算不准返回 false，交给重试。</returns>
    private bool PlaceAtDefaultCorner(ScreenArea workingArea, double scaling)
    {
        var corner = WindowPlacement.DefaultCorner(workingArea, scaling, _settings.Diameter);
        // 喵~防御：还算不准就先不摆，交给重试——绝不能拿坏值算出一个「最保守的位置」。
        if (corner is null)
        {
            return false;
        }

        Position = new PixelPoint(corner.Value.X, corner.Value.Y);
        _placementSettled = true;
        // 这是我们自己按屏幕信息算出来的位置，可信，可以存回配置。
        _positionTrustworthy = true;
        return true;
    }

    /// <summary>开一个短命的重试计时器，把没摆成功的位补上。</summary>
    /// <remarks>
    /// 只在「开机那一瞬间显示器信息还没就绪」时才真的用得上，
    /// 正常情况下 <see cref="RestorePosition"/> 第一次就成功，这里直接返回。
    /// </remarks>
    private void StartPositionRetry()
    {
        // 摆位阶段已经结束了就不用重试。
        if (_placementSettled)
        {
            return;
        }

        _positionRetryCount = 0;
        _positionRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionRetryTimer.Tick += (_, _) =>
        {
            // 每次重试都先试着摆一次。
            var placed = RestorePosition();
            // 摆成功、或者试够次数了，就收工。
            if (placed || ++_positionRetryCount >= MaxPositionRetries)
            {
                // 收工——不管摆成没摆成，摆位阶段到此为止。
                // 放弃时更要立起这个标记：不立的话 ClampToScreen 会永远不干活，
                // 之后主人把窗口拖出屏幕也没人管了。
                _placementSettled = true;
                _positionRetryTimer?.Stop();
                _positionRetryTimer = null;
            }
        };
        _positionRetryTimer.Start();
    }

    /// <summary>把 Avalonia 的屏幕矩形换成纯计算层的形式。</summary>
    private static ScreenArea ToArea(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>当前所有屏幕的矩形，用来判断「记位置时那块屏幕还在不在」。</summary>
    private IReadOnlyList<ScreenArea> CurrentScreenAreas()
    {
        var areas = new List<ScreenArea>();
        foreach (var screen in Screens.All)
        {
            areas.Add(ToArea(screen.Bounds));
        }

        return areas;
    }

    /// <summary>挑出窗口所在的屏幕。</summary>
    private Avalonia.Platform.Screen? PickScreen() =>
        Screens.ScreenFromWindow(this)
        ?? Screens.ScreenFromPoint(Position)
        ?? Screens.Primary;

    /// <summary>
    /// 把窗口夹回当前屏幕内。
    /// </summary>
    /// <remarks>
    /// 用的是 <c>Screen.Bounds</c> 而不是 <c>WorkingArea</c>——前者含任务栏区域。
    /// 也就是允许盖住任务栏，但不允许拖出屏幕。
    /// <para/>
    /// <b>拿不准的时候宁可不夹。</b>这个方法的调用点很密（位置一变、改大小、开窗都走它），
    /// 其中开窗那一次很可能撞上「显示器信息还没就绪」。那时候照常算会把位置钉死在屏幕左上角，
    /// 所以判断本身抽到了 <see cref="WindowPlacement.Clamp"/>，那里对坏值一律返回「不要动」。
    /// </remarks>
    private void ClampToScreen()
    {
        // 喵~防御：摆位阶段还没结束之前一律不夹。那段时间拿到的屏幕信息可能是假的
        // （非空、但尺寸是占位值），拿它夹取会把「还没恢复好的位置」钉到那个假屏幕上——
        // 之后重试成功也没用了，位置已经被改过。这正是
        // 「开机后悬浮钮位置不对、单独重启 ClassIsland 又正常」的成因：
        // 单独重启时系统早就绪，屏幕信息是真的，所以看不出问题。
        // 摆位阶段在 RestorePosition 摆成功后（或重试放弃时）结束。
        if (!_placementSettled)
        {
            return;
        }

        var screen = PickScreen();
        if (screen is null)
        {
            return;
        }

        var clamped = WindowPlacement.Clamp(
            (Position.X, Position.Y), ToArea(screen.Bounds), screen.Scaling, Width, Height);
        // 喵~防御：算不准（返回 null）就保持原样，而不是夹到一个更保守的位置。
        if (clamped is null)
        {
            return;
        }

        if (clamped.Value.X != Position.X || clamped.Value.Y != Position.Y)
        {
            Position = new PixelPoint(clamped.Value.X, clamped.Value.Y);
        }
    }

    /// <summary>把当前位置、以及当前位置所在屏幕的尺寸一起写回设置。</summary>
    /// <remarks>
    /// <b>只有「位置确实摆对了」或者「主人自己拖动过」时才记。</b>
    /// 窗口刚建出来时停在系统给的默认位置 (0,0)，那会儿屏幕信息也可能还没就绪。
    /// 这时候记下来的就是 (0,0)，一存盘就把上一次存好的位置覆盖掉了——
    /// 下次开机读到 (0,0)、继续存 (0,0)，就此卡死在屏幕左上角，自己永远好不了。
    /// <para/>
    /// <b>这正是 2026-09-16 主人报的「开机后悬浮钮跑到左上角、只重启 ClassIsland 又正常」。</b>
    /// 单独重启时屏幕信息立刻可用，摆位一次就成功，所以看不出问题；
    /// 开机那次屏幕信息迟到，摆位失败 → 位置停在 (0,0) → 退出时把 (0,0) 存了进去。
    /// </remarks>
    public void CapturePosition()
    {
        // 喵~防御：位置还没被确认过就不记。记下来的是坏值，比不记更糟——
        // 不记只是这一次开机位置不对，记了就是下一次也一起坏。
        if (!_positionTrustworthy)
        {
            return;
        }

        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;

        // 顺手记下这块屏幕的尺寸，用来在下次启动时判断分辨率有没有变过。
        // 喵~防御：拿不到屏幕信息时不覆盖已有的记录——覆盖成 0 会让下次启动
        // 误判成「分辨率变过了」而白白重置一次位置。
        if (PickScreen() is { } screen)
        {
            _settings.WindowScreenWidth = screen.Bounds.Width;
            _settings.WindowScreenHeight = screen.Bounds.Height;
        }
    }

    #endregion

    #region 交互

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            ShowMenu();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        _menuOpened = false;
        _pressOrigin = point.Position;
        // 记下抓取点在窗口内的物理偏移。拖动时用「屏幕坐标 - 这个偏移」直接定位，
        // 不去累加位移——窗口一边跟着动，窗口内相对坐标就会把位移抵消掉，越拖越跟不上。
        var pressOnScreen = this.PointToScreen(point.Position);
        _grabOffset = new PixelPoint(pressOnScreen.X - Position.X, pressOnScreen.Y - Position.Y);
        _knob.RenderTransform = TransformOperations.Parse("scale(0.93)");
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;

        // 触摸屏上没有右键，用长按代替。鼠标不走这条——鼠标按住不动很常见，
        // 弹菜单会很意外，它有真正的右键可用。
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            _holdTimer.Start();
        }

        e.Handled = true;
    }

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (!_pointerDown || _dragging)
        {
            return;
        }

        // 长按成立：弹菜单，并且标记一下，免得松手时又抽一个人。
        _menuOpened = true;
        _knob.RenderTransform = TransformOperations.Parse("scale(1)");
        ShowMenu();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown || _menuOpened)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _pressOrigin.X;
        var dy = current.Y - _pressOrigin.Y;

        if (!_dragging && Math.Sqrt(dx * dx + dy * dy) < DragThreshold)
        {
            return;
        }

        // 动起来了就不算长按了。
        _holdTimer.Stop();
        _dragging = true;

        // PointToScreen 已经把逻辑坐标换算成物理像素了，直接减掉抓取偏移就是新位置。
        var pointerOnScreen = this.PointToScreen(current);
        Position = new PixelPoint(
            pointerOnScreen.X - _grabOffset.X,
            pointerOnScreen.Y - _grabOffset.Y);
        ClampToScreen();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        _holdTimer.Stop();
        _pointerDown = false;
        _knob.RenderTransform = TransformOperations.Parse("scale(1)");
        e.Pointer.Capture(null);
        _capturedPointer = null;

        if (_menuOpened)
        {
            // 长按已经把菜单弹出来了，这一下不算点击。
            _menuOpened = false;
        }
        else if (_dragging)
        {
            _dragging = false;
            // 主人亲手摆的位置，当然可信——不管前面自动摆位有没有成功。
            _positionTrustworthy = true;
            CapturePosition();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.InitialPressMouseButton is MouseButton.Left or MouseButton.None)
        {
            // 动画正在播的时候，这一下不是「再抽一次」，而是「跳过动画」——
            // 钮上此刻显示的也是「跳」。抽选本身早就定好了，跳过只是不看表演。
            if (_busy == PickerBusyKind.Animating)
            {
                SkipRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                PickRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        e.Handled = true;
    }

    #endregion

    #region 设置菜单

    /// <summary>
    /// 弹出设置菜单。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="MenuFlyout"/> 而**不是** <c>ContextMenu</c>。
    /// 原因是查出来的实打实的坑：ClassIsland 的控件主题只来自 FluentAvalonia，
    /// 仓库里没有引用 Avalonia.Themes.Fluent，而 FluentAvalonia 2.4.1 压根不提供
    /// <c>ContextMenu</c> 的 ControlTheme（DLL 里连这个类型名都没有）。
    /// Avalonia 找隐式主题只按 StyleKey 精确匹配、没有基类回退，
    /// 于是 ContextMenu 的 Template 是 null → 不产生任何可视子级 → 量出来 0×0，
    /// 弹出窗口是创建了，但屏幕上什么都没有，表现就是「右键没反应」。
    /// MenuFlyout / MenuFlyoutPresenter / MenuItem / Separator 都被 FluentAvalonia 完整主题化了，
    /// 这也是 ClassIsland 自己全仓库的写法（都是 ContextFlyout + MenuFlyout）。
    /// </remarks>
    private void ShowMenu()
    {
        // 触摸长按时指针还被窗口捕获着，捕获期间事件不会进弹出层——
        // 菜单能弹出来但点不动。弹之前先把捕获放掉。
        _capturedPointer?.Capture(null);
        _capturedPointer = null;

        _menu ??= new MenuFlyout();
        _menu.ItemsSource = BuildMenuItems();
        // showAtPointer: 在当前指针位置弹，等价于原来的 PlacementMode.Pointer。
        _menu.ShowAt(_knob, showAtPointer: true);
    }

    /// <summary>单选组的组名。三组必须各不相同，否则会被归成一组互相抢。</summary>
    private const string ModeGroup = "picker.mode";

    private const string SizeGroup = "picker.size";
    private const string HoldGroup = "picker.hold";

    /// <summary>抽选动画样式的单选组名。</summary>
    private const string AnimGroup = "picker.anim";

    /// <summary>
    /// 菜单内容。每次打开都重新构建，勾选状态和剩余人数才是当前的。
    /// </summary>
    private object[] BuildMenuItems()
    {
        var total = _roster.Names.Count;
        var remaining = _roster.RemainingInRound(_settings);

        return
        [
            Header(_settings.Mode == PickMode.Photo
                ? "拍照抽人"
                : total == 0
                    ? "名单是空的"
                    : _settings.Mode == PickMode.NoRepeat
                        ? $"本轮还剩 {remaining} / {total} 人"
                        : $"共 {total} 人"),
            Item("抽一个", () => PickRequested?.Invoke(this, EventArgs.Empty)),
            new Separator(),

            Choice("随机抽选", ModeGroup, _settings.Mode == PickMode.Random,
                () => SetMode(PickMode.Random)),
            Choice("本轮内不重复", ModeGroup, _settings.Mode == PickMode.NoRepeat,
                () => SetMode(PickMode.NoRepeat)),
            Choice("拍照抽人", ModeGroup, _settings.Mode == PickMode.Photo,
                () => SetMode(PickMode.Photo)),
            Item("开始新一轮", () =>
            {
                RosterService.ResetRound(_settings);
                RefreshCounter();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }),
            new Separator(),

            Item("打开名单文件", OpenRosterFile),
            Item("重新载入名单", () =>
            {
                _roster.Reload();
                RefreshCounter();
            }),
            new Separator(),

            new MenuItem
            {
                Header = "悬浮窗大小",
                ItemsSource = new object[]
                {
                    Choice("小", SizeGroup, _settings.Size == PickerSize.Small,
                        () => SetSize(PickerSize.Small)),
                    Choice("中", SizeGroup, _settings.Size == PickerSize.Medium,
                        () => SetSize(PickerSize.Medium)),
                    Choice("大", SizeGroup, _settings.Size == PickerSize.Large,
                        () => SetSize(PickerSize.Large))
                }
            },
            new MenuItem
            {
                Header = "抽选动画",
                ItemsSource = new object[]
                {
                    // 五选一：样式本身在设置页里也能改，这里是为了随手切。
                    Choice("无动画", AnimGroup, _settings.AnimationStyle == RevealAnimationStyle.None,
                        () => SetAnimation(RevealAnimationStyle.None)),
                    Choice("滚动名字", AnimGroup, _settings.AnimationStyle == RevealAnimationStyle.Scroll,
                        () => SetAnimation(RevealAnimationStyle.Scroll)),
                    Choice("CSGO 开箱", AnimGroup, _settings.AnimationStyle == RevealAnimationStyle.Csgo,
                        () => SetAnimation(RevealAnimationStyle.Csgo)),
                    Choice("老虎机", AnimGroup, _settings.AnimationStyle == RevealAnimationStyle.Slot,
                        () => SetAnimation(RevealAnimationStyle.Slot)),
                    Choice("拼多多转盘", AnimGroup, _settings.AnimationStyle == RevealAnimationStyle.Wheel,
                        () => SetAnimation(RevealAnimationStyle.Wheel)),
                    new Separator(),
                    // 时长不放在菜单里：菜单只负责随手切样式，调时长去设置页。
                    Header("时长在「设置 → 随机抽选」里调")
                }
            },
            new MenuItem
            {
                Header = "抽中之后",
                ItemsSource = new object[]
                {
                    Toggle("屏幕中央弹出", _settings.ShowCenterReveal, value =>
                    {
                        _settings.ShowCenterReveal = value;
                        SettingsChanged?.Invoke(this, EventArgs.Empty);
                    }),
                    Toggle("发送 ClassIsland 提醒", _settings.ShowNotification, value =>
                    {
                        _settings.ShowNotification = value;
                        SettingsChanged?.Invoke(this, EventArgs.Empty);
                    }),
                    new Separator(),
                    Choice("停留 1.5 秒", HoldGroup, Math.Abs(_settings.RevealSeconds - 1.5) < 0.01,
                        () => SetHold(1.5)),
                    Choice("停留 2.5 秒", HoldGroup, Math.Abs(_settings.RevealSeconds - 2.5) < 0.01,
                        () => SetHold(2.5)),
                    Choice("停留 4 秒", HoldGroup, Math.Abs(_settings.RevealSeconds - 4.0) < 0.01,
                        () => SetHold(4.0))
                }
            },
            new Separator(),

            Item("隐藏悬浮窗", () => HideRequested?.Invoke(this, EventArgs.Empty))
        ];
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// 真·布尔开关。
    /// </summary>
    /// <remarks>
    /// <b>处理器里不能自己取反。</b>Avalonia 的 <see cref="MenuItem"/> 在点击时会先自行翻转
    /// <c>IsChecked</c>，处理器再取反一次就等于翻了两次。所以这里读控件翻好的值往设置里写。
    /// </remarks>
    private static MenuItem Toggle(string header, bool isChecked, Action<bool> apply)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked
        };
        item.Click += (_, _) => apply(item.IsChecked);
        return item;
    }

    /// <summary>
    /// 一组里只能选一个的选项。
    /// </summary>
    /// <remarks>
    /// <b>互斥项必须用 Radio 并且给 GroupName，不能用 CheckBox。</b>
    /// 用 CheckBox 时 Avalonia 只会机械地翻转被点的那一项：
    /// 点已选中的项会把它取消掉（整组一个勾都没有），点另一项则两个都带勾——
    /// 「菜单勾选有问题」就是这么来的。换成 Radio 之后，
    /// Avalonia 的 RadioButtonGroupManager 会自动取消同组的其它项，
    /// 点已选中的项也会保持选中。
    /// <para/>
    /// GroupName <b>必须每组不同</b>：它是分组的唯一依据，不给名字的话
    /// 三组九项会被归进同一组，互相抢。
    /// </remarks>
    private static MenuItem Choice(string header, string groupName, bool isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            GroupName = groupName,
            IsChecked = isChecked
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Header(string text) => new() { Header = text, IsEnabled = false };

    private void SetMode(PickMode mode)
    {
        _settings.Mode = mode;
        RefreshCounter();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSize(PickerSize size)
    {
        _settings.Size = size;
        ApplySize();
        CapturePosition();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHold(double seconds)
    {
        _settings.RevealSeconds = seconds;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换抽选动画样式。</summary>
    private void SetAnimation(RevealAnimationStyle style)
    {
        _settings.AnimationStyle = style;
        // 通知宿主服务把设置落盘。
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenRosterFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_roster.RosterPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序就算了。
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _holdTimer.Stop();
        // 摆位重试也要停掉，否则窗口都关了它还在空转。
        _positionRetryTimer?.Stop();
        _positionRetryTimer = null;
        _menu?.Hide();
        _topmost?.Dispose();
        base.OnClosed(e);
    }
}
