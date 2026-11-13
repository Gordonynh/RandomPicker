using System;
using System.Collections.Generic;
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
/// <para/>
/// <b>文字一行一个 TextBlock。</b>早先把带 <c>\n</c> 的整段文字塞进同一个 TextBlock，
/// 靠 TextAlignment 居中；行宽不等时每一行都会按自己的测量结果偏移，
/// 抽小组（组名 + 组员）或一次抽多人时歪得很明显。拆成逐行后每行独立居中于卡片中线。
/// </remarks>
public class RevealWindow : Window
{
    private static RevealWindow? _instance;

    private readonly StackPanel _nameLines;
    private readonly WrapPanel _portraits;
    private readonly StackPanel _captionLines;
    private readonly StackPanel _content;
    private readonly Border _card;
    private readonly DispatcherTimer _closeTimer;
    private TopmostEnforcer? _topmost;
    private bool _closing;

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
    public static void Show(Bitmap portrait, string? caption, double height, TimeSpan hold, Color accent) =>
        Show([portrait], caption, height, hold, accent);

    /// <summary>
    /// 一次显示多张人像。URL / 自动化「一次抽多人」在拍照模式下走这条。
    /// 人多了会自动缩小并换行，不会把卡片撑出屏幕。
    /// </summary>
    public static void Show(IReadOnlyList<Bitmap> portraits, string? caption, double height,
        TimeSpan hold, Color accent)
    {
        if (portraits.Count == 0)
        {
            return;
        }

        Ensure(hold, accent, height * 0.2).ShowPortraits(portraits, caption, height, hold, accent);
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

        _portraits = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };

        _captionLines = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            IsVisible = false
        };

        _nameLines = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        FillLines(_nameLines, name, fontSize, Math.Max(360, fontSize * 6),
            FontWeight.SemiBold, 1.0);

        _content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _portraits, _nameLines, _captionLines }
        };

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
            Child = _content,
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

    /// <summary>
    /// 把一段可能含换行的文字拆成一行一个 TextBlock。
    /// 每个 TextBlock 自己水平居中，整叠 StackPanel 也居中，行宽不同就不会逐行歪掉。
    /// </summary>
    private static void FillLines(StackPanel panel, string? text, double fontSize,
        double maxWidth, FontWeight weight, double opacity)
    {
        panel.Children.Clear();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = new SolidColorBrush(Colors.White, opacity),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxWidth
            });
        }
    }

    /// <summary>复用当前窗口显示一段文字（可含换行）。</summary>
    private void ShowText(string text, string? caption, double fontSize, TimeSpan hold, Color accent)
    {
        _portraits.IsVisible = false;
        _portraits.Children.Clear();

        FillLines(_nameLines, text, fontSize, Math.Max(360, fontSize * 6),
            FontWeight.SemiBold, 1.0);
        _nameLines.IsVisible = true;

        FillLines(_captionLines, caption, 20, Math.Max(240, fontSize * 5),
            FontWeight.Normal, 0.62);
        _captionLines.IsVisible = !string.IsNullOrEmpty(caption);

        _card.CornerRadius = new CornerRadius(fontSize * 0.18);
        _card.Padding = new Thickness(fontSize * 0.62, fontSize * 0.34);
        Bump(hold, accent);
    }

    /// <summary>复用当前窗口显示一张或多张人像。</summary>
    private void ShowPortraits(IReadOnlyList<Bitmap> portraits, string? caption,
        double height, TimeSpan hold, Color accent)
    {
        _nameLines.IsVisible = false;
        _portraits.IsVisible = true;
        _portraits.Children.Clear();

        // 人越多每张越小，最多缩到四成；WrapPanel 会自动换行。
        var scale = portraits.Count <= 1
            ? 1.0
            : Math.Clamp(1.0 / Math.Sqrt(portraits.Count), 0.4, 0.85);
        foreach (var portrait in portraits)
        {
            _portraits.Children.Add(new Image
            {
                Source = portrait,
                Stretch = Stretch.Uniform,
                Height = height * scale,
                Margin = new Thickness(6)
            });
        }

        FillLines(_captionLines, caption, 20, Math.Max(240, height * 0.9),
            FontWeight.Normal, 0.62);
        _captionLines.IsVisible = !string.IsNullOrEmpty(caption);

        _card.CornerRadius = new CornerRadius(18);
        _card.Padding = new Thickness(18);
        Bump(hold, accent);
    }

    /// <summary>缩一下再弹回来，给出「换了一个」的反馈，比原地换内容更容易察觉。</summary>
    private void Bump(TimeSpan hold, Color accent)
    {
        _closeTimer.Stop();
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
