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
        if (_busy)
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
    /// 拍照抽人的忙碌态。
    /// </summary>
    /// <remarks>
    /// 开摄像头 + 拍照 + 检测要一两秒，钮上必须有反馈，
    /// 否则用户会以为没点上而反复戳。
    /// </remarks>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            _label.Text = "📷";
            _counter.Text = "拍摄中";
            return;
        }

        RefreshCounter();
    }

    private bool _busy;

    #endregion

    #region 位置

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (_settings.WindowX == int.MinValue || _settings.WindowY == int.MinValue)
        {
            // 首次运行：摆在主屏右下角靠里一点的位置。
            if (screen is not null)
            {
                var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var size = (int)Math.Ceiling(_settings.Diameter * scaling);
                Position = new PixelPoint(
                    screen.WorkingArea.X + screen.WorkingArea.Width - size - (int)(48 * scaling),
                    screen.WorkingArea.Y + screen.WorkingArea.Height - size - (int)(48 * scaling));
            }
        }
        else
        {
            Position = new PixelPoint(_settings.WindowX, _settings.WindowY);
        }

        ClampToScreen();
        ApplyAccent();

        _topmost = new TopmostEnforcer(this);
        _topmost.Attach();
    }

    /// <summary>
    /// 把窗口夹回当前屏幕内。
    /// </summary>
    /// <remarks>
    /// 用的是 <c>Screen.Bounds</c> 而不是 <c>WorkingArea</c>——前者含任务栏区域。
    /// 也就是允许盖住任务栏，但不允许拖出屏幕。
    /// </remarks>
    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this)
                     ?? Screens.ScreenFromPoint(Position)
                     ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)Math.Ceiling(Width * scaling);
        var height = (int)Math.Ceiling(Height * scaling);

        var maxX = Math.Max(bounds.X, bounds.X + bounds.Width - width);
        var maxY = Math.Max(bounds.Y, bounds.Y + bounds.Height - height);
        var x = Math.Clamp(Position.X, bounds.X, maxX);
        var y = Math.Clamp(Position.Y, bounds.Y, maxY);

        if (x != Position.X || y != Position.Y)
        {
            Position = new PixelPoint(x, y);
        }
    }

    /// <summary>把当前位置写回设置。</summary>
    public void CapturePosition()
    {
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
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
            CapturePosition();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.InitialPressMouseButton is MouseButton.Left or MouseButton.None)
        {
            PickRequested?.Invoke(this, EventArgs.Empty);
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
        _menu?.Hide();
        _topmost?.Dispose();
        base.OnClosed(e);
    }
}
