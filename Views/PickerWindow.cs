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
    private readonly PickerHostService? _host;
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
    private bool _hidden;
    private int _menuSerial;
    private Point _pressOrigin;
    private PixelPoint _grabOffset;

    public PickerWindow(PickerSettings settings, RosterService roster, PickerHostService? host = null)
    {
        _settings = settings;
        _roster = roster;
        _host = host;

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
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = double.PositiveInfinity
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

    /// <summary>当前是不是被藏起来了。</summary>
    public bool IsHidden => _hidden;

    #region 显示 / 隐藏

    /// <summary>
    /// 显示或隐藏悬浮窗。
    /// </summary>
    /// <remarks>
    /// <b>隐藏时必须先把 <see cref="TopmostEnforcer"/> 挂起</b>：那个定时器每秒重申一次置顶，
    /// 隐藏期间继续跑不但白费，历史上还因为带了 <c>SWP_SHOWWINDOW</c> 把窗口又拉了出来，
    /// 表现就是「点隐藏没反应」。现在两处都改了：定时器不再带显示标志，隐藏时也会停掉。
    /// <para/>
    /// 重新显示走 <see cref="Window.Show"/>（Avalonia 允许 Hide 之后再次 Show），
    /// 并恢复置顶重申。状态由宿主写进 settings.json，重启后仍然记得。
    /// </remarks>
    public void SetHidden(bool hidden)
    {
        if (_hidden == hidden)
        {
            return;
        }

        _hidden = hidden;
        if (hidden)
        {
            _menu?.Hide();
            _holdTimer.Stop();
            _flashTimer?.Stop();
            _topmost?.Suspend();
            Hide();
            return;
        }

        Show();
        _topmost?.Resume();
        ApplyAccent();
        RefreshCounter();
    }

    #endregion

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

    /// <summary>刷新圆钮上的剩余人数 / 组数。</summary>
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

        if (_settings.Mode == PickMode.Group)
        {
            var groups = GroupService.GroupCount(total, _settings.GroupSize);
            var left = Math.Max(0, groups - (_settings.DrawnGroupsThisRound?.Count ?? 0));
            _counter.Text = $"{left}/{groups} 组";
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

    /// <summary>临时提示的回滚定时器（如「课间禁用」）。</summary>
    private DispatcherTimer? _flashTimer;

    /// <summary>
    /// 在圆钮上闪一条两秒的临时提示，然后恢复正常的计数显示。
    /// </summary>
    /// <remarks>课间禁用时点一下圆钮什么都不发生会很莫名其妙，得告诉用户为什么没反应。</remarks>
    public void FlashMessage(string text)
    {
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashElapsed;
        _flashTimer.Tick += OnFlashElapsed;

        _label.Text = text;
        _label.FontSize = _settings.Diameter * 0.17;
        _counter.Text = string.Empty;
        _flashTimer.Start();
    }

    private void OnFlashElapsed(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        _label.FontSize = _settings.Diameter * 0.30;
        RefreshCounter();
    }

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

    /// <summary>
    /// 一屏菜单里的一个单选组。
    /// </summary>
    /// <remarks>
    /// <b>不能只靠 Avalonia 的 Radio + GroupName 自动互斥。</b>
    /// RadioButtonGroupManager 的分组表是在菜单项挂到可视树时建立的，
    /// 而这里每次右键都新建一批 MenuItem；建表时表还是空的，旧项又可能还没注销，
    /// 于是「两个圆点同时亮」。这里做两件事：
    /// <list type="number">
    /// <item>每次弹菜单给每组换一个唯一的 GroupName，旧项和新项不会被归进同一组；</item>
    /// <item>点击时自己把同组其它项显式置为未选中，不依赖分组表。</item>
    /// </list>
    /// 切换名单那组也一样，菜单重开时勾选状态按当前设置重新取。
    /// </remarks>
    private sealed class RadioGroup
    {
        private readonly List<MenuItem> _items = [];
        private readonly string _name;

        public RadioGroup(string name) => _name = name;

        public MenuItem Add(string header, bool isChecked, Action action)
        {
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = _name,
                IsChecked = isChecked
            };
            item.Click += (_, _) =>
            {
                foreach (var other in _items)
                {
                    other.IsChecked = ReferenceEquals(other, item);
                }

                action();
            };
            _items.Add(item);
            return item;
        }
    }

    /// <summary>
    /// 菜单内容。每次打开都重新构建，勾选状态和剩余人数才是当前的。
    /// </summary>
    private object[] BuildMenuItems()
    {
        // 组名带一个自增后缀：新旧菜单项不会落进 Avalonia 全局分组表的同一个桶。
        var suffix = "." + (++_menuSerial);
        var mode = new RadioGroup("picker.mode" + suffix);
        var size = new RadioGroup("picker.size" + suffix);
        var hold = new RadioGroup("picker.hold" + suffix);
        var roster = new RadioGroup("picker.roster" + suffix);

        var total = _roster.Names.Count;
        var remaining = _roster.RemainingInRound(_settings);
        var groupTotal = GroupService.GroupCount(total, _settings.GroupSize);
        var groupRemaining = Math.Max(0, groupTotal - (_settings.DrawnGroupsThisRound?.Count ?? 0));

        return
        [
            Header(_settings.Mode == PickMode.Photo
                ? "拍照抽人"
                : total == 0
                    ? "名单是空的"
                    : _settings.Mode == PickMode.Group
                        ? $"本轮还剩 {groupRemaining} / {groupTotal} 组"
                        : _settings.Mode == PickMode.NoRepeat
                            ? $"本轮还剩 {remaining} / {total} 人"
                            : $"共 {total} 人"),
            Item("抽一个", () => PickRequested?.Invoke(this, EventArgs.Empty)),
            new Separator(),

            mode.Add("随机抽选", _settings.Mode == PickMode.Random,
                () => SetMode(PickMode.Random)),
            mode.Add("本轮内不重复", _settings.Mode == PickMode.NoRepeat,
                () => SetMode(PickMode.NoRepeat)),
            mode.Add("小组抽选", _settings.Mode == PickMode.Group,
                () => SetMode(PickMode.Group)),
            mode.Add("拍照抽人", _settings.Mode == PickMode.Photo,
                () => SetMode(PickMode.Photo)),
            Item("开始新一轮", () =>
            {
                if (_settings.Mode == PickMode.Group)
                {
                    GroupService.ResetRound(_settings);
                }
                else
                {
                    RosterService.ResetRound(_settings);
                }

                RefreshCounter();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }),
            new Separator(),

            Item("打开当前名单", OpenRosterFile),
            Item("重新载入名单", () =>
            {
                _roster.Reload();
                RefreshCounter();
            }),
            new MenuItem
            {
                Header = "切换名单",
                ItemsSource = BuildRosterItems(roster)
            },
            new Separator(),

            new MenuItem
            {
                Header = "悬浮窗大小",
                ItemsSource = new object[]
                {
                    size.Add("小", _settings.Size == PickerSize.Small,
                        () => SetSize(PickerSize.Small)),
                    size.Add("中", _settings.Size == PickerSize.Medium,
                        () => SetSize(PickerSize.Medium)),
                    size.Add("大", _settings.Size == PickerSize.Large,
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
                    hold.Add("停留 1.5 秒", Math.Abs(_settings.RevealSeconds - 1.5) < 0.01,
                        () => SetHold(1.5)),
                    hold.Add("停留 2.5 秒", Math.Abs(_settings.RevealSeconds - 2.5) < 0.01,
                        () => SetHold(2.5)),
                    hold.Add("停留 4 秒", Math.Abs(_settings.RevealSeconds - 4.0) < 0.01,
                        () => SetHold(4.0))
                }
            },
            new Separator(),

            Item("隐藏悬浮窗", () => HideRequested?.Invoke(this, EventArgs.Empty))
        ];
    }

    /// <summary>「切换名单」子菜单，每项显示该份名单的人数。</summary>
    private object[] BuildRosterItems(RadioGroup group)
    {
        if (_host is null)
        {
            return [Header("插件未就绪")];
        }

        var items = new object[PickerHostService.RosterSlots];
        for (var i = 1; i <= PickerHostService.RosterSlots; i++)
        {
            var index = i;
            items[i - 1] = group.Add(_host.RosterLabel(index),
                _settings.ActiveRosterIndex == index,
                () =>
                {
                    _host.SwitchRoster(index);
                    RefreshCounter();
                });
        }

        return items;
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
        _flashTimer?.Stop();
        _menu?.Hide();
        _topmost?.Dispose();
        base.OnClosed(e);
    }
}
