using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Interop;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views.Animations;

namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 屏幕中央的大字弹窗。抽到谁就把名字亮在屏幕中间，停留几秒后自己淡出。
/// </summary>
/// <remarks>
/// 两个关键设计：
/// <list type="bullet">
/// <item><b>窗口只有卡片那么大</b>，不铺满屏幕。早先版本是整屏窗口，结果它把悬浮钮盖住了，
///       连着抽人时第二下点的是它而不是钮，表现就是「点不动」。
///       试过用 <c>WS_EX_TRANSPARENT</c> 让它穿透，但那个样式只在窗口同时是 LAYERED 时才对命中测试生效，
///       而给 Avalonia 的透明窗口补 <c>WS_EX_LAYERED</c> 会打乱它自己的合成，窗口直接不显示。
///       两条路都堵死，索性从根上解决：窗口不覆盖屏幕，就没有挡不挡的问题。</item>
/// <item><b>单例复用</b>。再抽一次不新开窗口，直接换掉里面的名字并重置计时，
///       避免连点时叠出一摞窗口。</item>
/// </list>
/// </remarks>
public class RevealWindow : Window
{
    private static RevealWindow? _instance;

    private readonly TextBlock _nameText;
    private readonly Image _portrait;
    private readonly TextBlock _caption;
    private readonly StackPanel _content;
    private readonly Border _card;
    private readonly DispatcherTimer _closeTimer;
    private TopmostEnforcer? _topmost;
    private bool _closing;

    /// <summary>动画层。平时藏着，播动画时才露面。</summary>
    private readonly RevealAnimationHost _animationHost = new();

    /// <summary>当前这段动画的取消令牌源。换一段就换一个新的。</summary>
    private CancellationTokenSource? _animationCts;

    /// <summary>这一次取消是不是「用户点了跳过」。</summary>
    /// <remarks>
    /// 取消有两种完全相反的含义，得区分开：
    /// <list type="bullet">
    /// <item><b>跳过</b>：用户不想看表演了，要<b>立刻看到结果</b>——收尾照做。</item>
    /// <item><b>别的取消</b>（要换下一段动画、窗口要关了）：绝不能碰进度属性，
    ///       否则复用的控件会残留上一轮的结果。</item>
    /// </list>
    /// </remarks>
    private bool _skipRequested;

    /// <summary>
    /// 立刻结束正在播的动画，直接定格出结果。
    /// </summary>
    /// <remarks>用户点了悬浮钮上的「跳」。中选者早就定好了，跳过只是不看表演。</remarks>
    internal static void SkipAnimation() => _instance?.SkipCurrentAnimation();

    /// <summary>
    /// 播一段抽选动画，播完再出结果。
    /// </summary>
    /// <param name="plan">排片表。中选者早就定好了，动画只负责把它演出来。</param>
    /// <param name="fontSize">中央大字的字号档位，动画尺寸按它换算。</param>
    /// <param name="accent">主题强调色。</param>
    /// <param name="hold">动画结束后结果停留多久。</param>
    /// <param name="onCompleted">动画播完（或被跳过）之后要做的事——展示结果、发提醒都在那里。</param>
    internal static void Play(RevealAnimationPlan plan, double fontSize, Color accent,
        TimeSpan hold, Action onCompleted) =>
        Ensure(hold, accent, fontSize).PlayAnimation(plan, fontSize, accent, hold, onCompleted);

    /// <summary>
    /// 显示一个名字。已经有窗口开着就复用它，否则新建。
    /// </summary>
    public static void Show(string text, double fontSize, TimeSpan hold, Color accent) =>
        Show(text, null, fontSize, hold, accent);

    /// <summary>显示一个名字，下面可以再带一行小字说明。</summary>
    public static void Show(string text, string? caption, double fontSize, TimeSpan hold, Color accent)
    {
        Ensure(hold, accent, fontSize).ShowText(text, caption, fontSize, hold, accent);
    }

    /// <summary>
    /// 显示一张人像。拍照抽人走这条。
    /// </summary>
    /// <param name="portrait">裁好的人像。</param>
    /// <param name="caption">人像下面的一行小字，没有就传 null。</param>
    /// <param name="height">人像显示高度（逻辑像素）。</param>
    public static void Show(Bitmap portrait, string? caption, double height, TimeSpan hold, Color accent)
    {
        Ensure(hold, accent, height * 0.2).ShowPortrait(portrait, caption, height, hold, accent);
    }

    /// <summary>拿到可用的窗口实例：已经开着就复用，否则新建。</summary>
    private static RevealWindow Ensure(TimeSpan hold, Color accent, double fontSize)
    {
        if (_instance is { _closing: false } existing)
        {
            return existing;
        }

        var window = new RevealWindow(string.Empty, fontSize, hold, accent);
        _instance = window;
        window.Show();
        return window;
    }

    /// <summary>把当前开着的弹窗立刻收掉（比如插件停止时）。</summary>
    public static void CloseCurrent() => _instance?.FadeOutAndClose();

    private RevealWindow(string name, double fontSize, TimeSpan hold, Color accent)
    {
        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        // 窗口按卡片大小自适应，居中显示。不铺满屏幕，才不会挡住悬浮钮。
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        IsHitTestVisible = false;

        _portrait = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };

        _caption = new TextBlock
        {
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.White, 0.62),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            IsVisible = false
        };

        _nameText = new TextBlock
        {
            Text = name,
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _portrait, _nameText, _caption },
            // 结果层一开始是藏着的：动画先上，动画结束才交叉淡入换到它。
            IsVisible = false,
            Opacity = 0,
            Transitions =
            [
                // 淡入用的过渡。DoubleTransition 不会从泛型参数推出属性名，
                // 必须显式写上 Property——漏了会在构造窗口时直接抛
                //「Transition has no property specified.」，整个插件都用不了。
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(160)
                }
            ]
        };

        // 动画层和结果层叠在一起，靠 IsVisible 切换。
        // 结果层始终留在可视树里（只是藏着），换过来的时候不重建模板、不触发布局重建，所以不闪。
        var layers = new Panel { Children = { _animationHost, _content } };

        _card = new Border
        {
            // 深色玻璃质感 + 一条主题色细边。不铺全屏遮罩——连着抽人时整屏一明一暗很累眼。
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x17, 0x17, 0x1C)),
            CornerRadius = new CornerRadius(fontSize * 0.18),
            Padding = new Thickness(fontSize * 0.62, fontSize * 0.34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(accent, 0.55),
            BorderThickness = new Thickness(1.5),
            Child = layers,
            Opacity = 0,
            // 用 TransformOperations 而不是自己搭 ScaleTransform：
            // 过渡挂在 Border（一个 Visual）上，有自己的时钟能正常跑；
            // 单独的 ScaleTransform 不是 Visual，交给 Animation.RunAsync 会抛类型转换异常。
            RenderTransform = TransformOperations.Parse("scale(0.92)"),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            ]
        };

        Content = new Panel { Children = { _card } };

        _closeTimer = new DispatcherTimer { Interval = hold };
        _closeTimer.Tick += (_, _) => FadeOutAndClose();
    }

    /// <summary>复用当前窗口显示一个名字。</summary>
    private void ShowText(string text, string? caption, double fontSize, TimeSpan hold, Color accent)
    {
        // 上一次可能是播过动画的，那次留下的舞台尺寸要清掉，不然卡片会一直撑那么大。
        ReleaseStageSize();
        _portrait.IsVisible = false;
        _portrait.Source = null;
        _caption.IsVisible = !string.IsNullOrEmpty(caption);
        _caption.Text = caption ?? string.Empty;
        _caption.MaxWidth = Math.Max(240, fontSize * 5);
        _nameText.IsVisible = true;
        _nameText.Text = text;
        _nameText.FontSize = fontSize;

        _card.CornerRadius = new CornerRadius(fontSize * 0.18);
        _card.Padding = new Thickness(fontSize * 0.62, fontSize * 0.34);
        Bump(hold, accent);
    }

    /// <summary>复用当前窗口显示一张人像。</summary>
    private void ShowPortrait(Bitmap portrait, string? caption, double height, TimeSpan hold, Color accent)
    {
        // 同上：拍照这条路不播动画，别留着上一次动画的舞台尺寸。
        ReleaseStageSize();
        _nameText.IsVisible = false;
        _portrait.IsVisible = true;
        _portrait.Source = portrait;
        _portrait.Height = height;

        _caption.IsVisible = !string.IsNullOrEmpty(caption);
        _caption.Text = caption ?? string.Empty;

        _card.CornerRadius = new CornerRadius(18);
        _card.Padding = new Thickness(18);
        Bump(hold, accent);
    }

    /// <summary>把结果层摆出来。</summary>
    /// <param name="immediate">true 表示直接显示不淡入；false 表示交叉淡入。</param>
    private void ShowContentLayer(bool immediate)
    {
        // 动画层收掉，结果层接上——这就是那个交叉过渡。
        _animationHost.IsVisible = false;
        _content.IsVisible = true;
        if (immediate)
        {
            // 不播动画的路径：直接显示，别拖那 160 毫秒。
            _content.Opacity = 1;
            return;
        }

        // 先归零，再让过渡把透明度推到 1。
        _content.Opacity = 0;
        Dispatcher.UIThread.Post(() => _content.Opacity = 1, DispatcherPriority.Render);
    }

    /// <summary>清掉上一次动画留下的舞台尺寸。</summary>
    private void ReleaseStageSize()
    {
        _card.MinWidth = 0;
        _card.MinHeight = 0;
        // 动画控件也一起卸掉，免得它带着上一轮的进度留在可视树里。
        _animationHost.Reset();
        _animationHost.IsVisible = false;
    }

    /// <summary>
    /// 播一段抽选动画，播完再出结果。
    /// </summary>
    /// <param name="plan">排片表。</param>
    /// <param name="fontSize">中央大字的字号档位。</param>
    /// <param name="accent">主题强调色。</param>
    /// <param name="hold">动画结束后结果停留多久。</param>
    /// <param name="onCompleted">播完之后要做的事。</param>
    private void PlayAnimation(RevealAnimationPlan plan, double fontSize, Color accent,
        TimeSpan hold, Action onCompleted)
    {
        // 动画期间绝不能被「停留计时」关掉窗口。
        _closeTimer.Stop();
        // 这一段还没开始播，先把「跳过」的标记清掉——上一段的标记不能漏到这一段来。
        _skipRequested = false;
        // 上一段还在播的话先掐掉，位置让给新的这一段。
        CancelAnimation();

        // 舞台尺寸一开始就定好，整个显示期间不再变——窗口只在中间那次布局里挪一次，不会抖。
        var stage = _animationHost.Load(plan, fontSize, accent);
        // 喵~防御：舞台尺寸没量出来（装载失败）时退化成直接出结果，绝不把结果吞掉。
        if (stage.Width <= 0 || stage.Height <= 0)
        {
            _animationHost.Reset();
            ShowContentLayer(immediate: true);
            onCompleted();
            _closeTimer.Interval = hold;
            _closeTimer.Start();
            return;
        }

        _card.MinWidth = stage.Width;
        _card.MinHeight = stage.Height;
        _animationHost.IsVisible = true;
        // 结果层让位，但留在可视树里等着接上。
        _content.IsVisible = false;
        _content.Opacity = 0;
        // 卡片本身亮起来（不播动画时这活儿是 Bump 干的）。
        _card.BorderBrush = new SolidColorBrush(accent, 0.55);
        _card.RenderTransform = TransformOperations.Parse("scale(1)");
        _card.Opacity = 1;
        _topmost?.Reassert();

        // 每段动画一个新令牌：上一段被取消时它的 finally 才不会误伤这一段。
        var cts = new CancellationTokenSource();
        _animationCts = cts;
        _ = RunAnimationAsync(cts.Token, hold, onCompleted);
    }

    /// <summary>跑完这段动画并收尾。</summary>
    private async Task RunAnimationAsync(CancellationToken token, TimeSpan hold, Action onCompleted)
    {
        try
        {
            await _animationHost.PlayAsync(token);
        }
        catch (OperationCanceledException)
        {
            // 被取消是正常路径（连点、窗口要关了），安静退出。
        }
        catch (Exception ex)
        {
            // 喵~防御：动画自己出岔子绝不能把结果吞掉——下面照常收尾，用户该看到的名字一个不少。
            // 但「吞掉」不等于「装作没发生」：留一行调试输出，
            // 否则动画控件里写错点什么，症状只是「动画没播」而没有任何线索。
            // 先例：2026-09-15 那次「动画并没有触发」，就是这里把 Cue 的 ArgumentException 咽了。
            System.Diagnostics.Debug.WriteLine($"[随机抽选] 动画播放失败，已直接出结果：{ex}");
        }

        // 喵~防御：被取消时一般什么都不做。硬置终值会让这个复用的控件在下一轮里残留上一次的结果。
        // 「用户点了跳过」是唯一的例外：他要的就是立刻看到结果，所以照常往下走完收尾。
        if (token.IsCancellationRequested && !_skipRequested)
        {
            return;
        }

        // 正常播完：把进度压到终点定格，再交叉淡入换到结果层。
        _animationHost.SnapToEnd();
        ShowContentLayer(immediate: false);

        // 计时从现在才开始：「停留 X 秒」只管动画结束之后停多久。
        _closeTimer.Interval = hold;
        _closeTimer.Start();

        // 结果和提醒都在这一刻才出现，免得提醒比动画还先到。
        onCompleted();
    }

    /// <summary>掐掉正在播的动画。窗口要关、或者要换下一段时调用。</summary>
    private void CancelAnimation()
    {
        var cts = _animationCts;
        // 先断开引用再取消：取消会同步跑进上面那个 finally，那里会看令牌是不是被取消过。
        _animationCts = null;
        cts?.Cancel();
    }

    /// <summary>用户点了「跳」：取消这一段，但照常收尾出结果。</summary>
    private void SkipCurrentAnimation()
    {
        // 喵~防御：没有动画在播（或者已经收完尾了）时没什么可跳的。
        // 少了这道判断，收尾之后残留的令牌会让结束逻辑跑第二遍，结果和提醒都会发两次。
        if (_animationCts is null)
        {
            return;
        }

        // 先立标记再取消：取消之后 <see cref="RunAnimationAsync"/> 会看到它是「跳过」而不是普通取消。
        _skipRequested = true;
        CancelAnimation();
    }

    /// <summary>缩一下再弹回来，给出「换了一个」的反馈，比原地换内容更容易察觉。</summary>
    private void Bump(TimeSpan hold, Color accent)
    {
        _closeTimer.Stop();
        // 这一次不播动画：把动画层收掉，结果层直接摆出来。
        ShowContentLayer(immediate: true);
        _card.BorderBrush = new SolidColorBrush(accent, 0.55);
        _card.RenderTransform = TransformOperations.Parse("scale(0.94)");
        Dispatcher.UIThread.Post(
            () => _card.RenderTransform = TransformOperations.Parse("scale(1)"),
            DispatcherPriority.Render);

        _card.Opacity = 1;
        _closeTimer.Interval = hold;
        _closeTimer.Start();
        _topmost?.Reassert();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        CenterOnScreen();
        SizeChanged += (_, _) => CenterOnScreen();

        _topmost = new TopmostEnforcer(this, TimeSpan.FromMilliseconds(400));
        _topmost.Attach();

        // 入场：设一次目标值，剩下的交给上面挂好的过渡。
        _card.Opacity = 1;
        _card.RenderTransform = TransformOperations.Parse("scale(1)");

        _closeTimer.Start();
    }

    /// <summary>
    /// 把窗口摆到当前屏幕正中。内容换了大小会变，所以 SizeChanged 时要重新摆一次。
    /// </summary>
    private void CenterOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Position = new PixelPoint(
            screen.Bounds.X + (screen.Bounds.Width - width) / 2,
            screen.Bounds.Y + (screen.Bounds.Height - height) / 2);
    }

    private void FadeOutAndClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _closeTimer.Stop();
        // 窗口要关了，正在播的动画没必要再跑下去。
        CancelAnimation();
        _card.Opacity = 0;
        _card.RenderTransform = TransformOperations.Parse("scale(0.96)");

        var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        fade.Tick += (_, _) =>
        {
            fade.Stop();
            _topmost?.Dispose();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }

            Close();
        };
        fade.Start();
    }
}
