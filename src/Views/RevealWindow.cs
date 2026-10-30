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
/// 屏幕中央的结果弹窗。抽中谁（或者哪一组）就把结果亮在中间，停留几秒后自己淡出。
/// </summary>
/// <remarks>
/// 两种内容共用一个窗口：文字（名字、组名，可多行）和拍照裁出来的人像。
/// 两个关键设计：
/// <list type="bullet">
/// <item><b>窗口只有卡片那么大</b>，不铺满屏幕。早先版本是整屏窗口，结果它把悬浮球盖住了，
///       连着抽人时第二下点的是它而不是球，表现就是「点不动」。
///       试过用 <c>WS_EX_TRANSPARENT</c> 让它穿透，但那个样式只在窗口同时是 LAYERED 时才对命中测试生效，
///       而给 Avalonia 的透明窗口补 <c>WS_EX_LAYERED</c> 会打乱它自己的合成，窗口直接不显示。
///       两条路都堵死，索性从根上解决：窗口不覆盖屏幕，就没有挡不挡的问题。</item>
/// <item><b>单例复用</b>。再抽一次不新开窗口，直接换掉里面的内容并重置计时，
///       避免连点时叠出一摞窗口。</item>
/// </list>
/// </remarks>
public class RevealWindow : Window
{
    private static RevealWindow? _instance;

    private readonly StackPanel _portraits;
    private readonly StackPanel _nameLines;
    private readonly TextBlock _caption;
    private readonly StackPanel _content;
    private readonly Border _card;
    private readonly DispatcherTimer _closeTimer;
    private TopmostEnforcer? _topmost;
    private bool _closing;

    /// <summary>
    /// 显示文字。<c>\n</c> 分行。已经有窗口开着就复用它，否则新建。
    /// </summary>
    public static void Show(string text, double fontSize, TimeSpan hold, Color accent) =>
        Show(text, null, fontSize, hold, accent);

    /// <summary>显示文字，下面可以再带一行小字说明。</summary>
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
    /// 一次显示好几张人像。拍照模式一次抽多个人时走这条。
    /// </summary>
    /// <param name="portraits">裁好的人像，按抽到的顺序排。</param>
    /// <param name="caption">人像下面的一行小字，没有就传 null。</param>
    /// <param name="height">单张人像的显示高度（逻辑像素）。多张时会等比缩一点，免得排不下。</param>
    public static void Show(IReadOnlyList<Bitmap> portraits, string? caption, double height, TimeSpan hold, Color accent)
    {
        Ensure(hold, accent, height * 0.2).ShowPortraits(portraits, caption, height, hold, accent);
    }

    /// <summary>拿到可用的窗口实例：已经开着就复用，否则新建。</summary>
    private static RevealWindow Ensure(TimeSpan hold, Color accent, double fontSize)
    {
        if (_instance is { _closing: false } existing)
        {
            return existing;
        }

        var window = new RevealWindow(fontSize, hold, accent);
        _instance = window;
        window.Show();
        return window;
    }

    /// <summary>把当前开着的弹窗立刻收掉（比如插件停止时）。</summary>
    public static void CloseCurrent() => _instance?.FadeOutAndClose();

    private RevealWindow(double fontSize, TimeSpan hold, Color accent)
    {
        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        // 窗口按卡片大小自适应，居中显示。不铺满屏幕，才不会挡住悬浮球。
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        IsHitTestVisible = false;

        // 一次抽多个人时，人像横着排开。
        _portraits = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
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

        // 一行一个 TextBlock，竖着排。不用一个 TextBlock 塞 \n——见 ShowText 里的说明。
        _nameLines = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _portraits, _nameLines, _caption }
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
    /// 复用当前窗口显示一到多行名字。
    /// </summary>
    /// <remarks>
    /// <b>每行单独一个 TextBlock，不把它们用 <c>\n</c> 拼进同一个 TextBlock。</b>
    /// 同一个 TextBlock 里靠 <c>TextAlignment.Center</c> 逐行居中是靠不住的：
    /// 各行宽度不一样时（「A 组」和「C 组」里字母的步进宽度不同），
    /// 上面那行会莫名其妙往右偏，行数越多偏得越厉害——看着就像左边距一段段变大。
    /// 抽纯中文名字时各行等宽，所以看不出来；抽字母组就露馅了。
    /// 拆成独立的 TextBlock 之后，每行各自居中，行宽差异不影响对齐。
    /// </remarks>
    private void ShowText(string text, string? caption, double fontSize, TimeSpan hold, Color accent)
    {
        ClearPortraits();
        ClearLines();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            _nameLines.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

        _nameLines.IsVisible = true;

        _caption.IsVisible = !string.IsNullOrEmpty(caption);
        _caption.Text = caption ?? string.Empty;
        _caption.MaxWidth = Math.Max(240, fontSize * 5);

        _card.CornerRadius = new CornerRadius(fontSize * 0.18);
        _card.Padding = new Thickness(fontSize * 0.62, fontSize * 0.34);
        Bump(hold, accent);
    }

    /// <summary>复用当前窗口显示一到多张人像。</summary>
    private void ShowPortraits(IReadOnlyList<Bitmap> portraits, string? caption, double height, TimeSpan hold, Color accent)
    {
        ClearPortraits();

        // 三张并排时按原高度会顶到屏幕边上，人多了就整体缩一点。
        var each = portraits.Count switch
        {
            <= 1 => height,
            2 => height * 0.82,
            _ => height * 0.68
        };

        foreach (var portrait in portraits)
        {
            _portraits.Children.Add(new Image
            {
                Source = portrait,
                Stretch = Stretch.Uniform,
                Height = each,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        _portraits.IsVisible = portraits.Count > 0;
        ClearLines();

        _caption.IsVisible = !string.IsNullOrEmpty(caption);
        _caption.Text = caption ?? string.Empty;

        _card.CornerRadius = new CornerRadius(18);
        _card.Padding = new Thickness(18);
        Bump(hold, accent);
    }

    /// <summary>把名字区清空。人像和文字是互斥的两套内容，换内容前得把另一套收掉。</summary>
    private void ClearLines()
    {
        _nameLines.Children.Clear();
        _nameLines.IsVisible = false;
    }

    /// <summary>把人像区清空并释放 Bitmap，不然连着抽会一直攒着图不放。</summary>
    private void ClearPortraits()
    {
        foreach (var child in _portraits.Children)
        {
            if (child is Image { Source: IDisposable bitmap })
            {
                bitmap.Dispose();
            }
        }

        _portraits.Children.Clear();
        _portraits.IsVisible = false;
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
