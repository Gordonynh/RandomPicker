using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Views.Animations;

/// <summary>
/// 四种抽选动画的共同底座。
/// </summary>
/// <remarks>
/// 整体架构是「<b>属性存进度，Render 现算几何</b>」：动画只负责把某个进度属性从起点推到终点，
/// 画面上长什么样完全由 <see cref="Render"/> 按当前进度算出来。
/// <para/>
/// 这么做的好处是几何成了纯函数——同样的进度必定画出同样的画面。
/// 抽选动画最容易出的两类 bug（转盘停在旁边那一格、老虎机的字和中选者对不上）
/// 都能靠「对着进度数字验算」定位，而不是靠反复看动画猜。
/// 宿主里的 <c>SlantedMaskControl</c> 用的也是这套写法。
/// <para/>
/// 每个子类都<b>必须</b>给出 <see cref="StageSize"/>。裸 <see cref="Control"/> 默认量出 0×0，
/// 那样画面上会是一片空白<b>而且不报任何错</b>，排查起来最费劲。
/// </remarks>
internal abstract class RevealAnimationBase : Control
{
    /// <summary>建立一个动画控件。</summary>
    /// <param name="revealFontSize">中央大字的字号档位，动画里所有尺寸都按它换算。</param>
    /// <param name="accent">主题强调色，用来画中选者的高亮。</param>
    protected RevealAnimationBase(double revealFontSize, Color accent)
    {
        // 喵~防御：字号配置被手改成 0、负数或 NaN 时，按它换算出来的尺寸全是坏值，
        // 量出来的舞台会是负的，整个卡片会塌成一条线。这里兜一个能看的字号。
        RevealFontSize = double.IsFinite(revealFontSize) && revealFontSize > 0 ? revealFontSize : 48;
        Accent = accent;
        // 动画只在自己的地盘里画，滚出界的内容一律裁掉。
        ClipToBounds = true;
        // 舞台尺寸是算好的固定值，居中摆，不让布局把它拉伸开。
        // 注意右边得写全限定名：类里已经有一个同名属性，光写 HorizontalAlignment.Center 会被解析成那个属性。
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    /// <summary>中央大字的字号档位。动画里所有尺寸都按它换算，保证和插件其它部分同一套视觉尺度。</summary>
    protected double RevealFontSize { get; }

    /// <summary>主题强调色。</summary>
    protected Color Accent { get; }

    /// <summary>这块动画需要多大地方。装好排片表之后才是准确值。</summary>
    public abstract Size StageSize { get; }

    /// <summary>
    /// 动作播完之后画面还要静止多久，单位：时间。装载排片表时从它上面抄下来。
    /// </summary>
    /// <remarks>
    /// 这段静止<b>不算在动画时长里</b>：动画只负责把进度推到终点，推完就地松手，
    /// 属性停在终值上不动（<c>FillMode.Forward</c> 在放下绑定时会把终值写成本地值）。
    /// <para/>
    /// <b>为什么不干脆把定格也算进 <c>Duration</c>：</b>那样会让关键帧的 cue 整体缩短。
    /// CSGO 那次就是这么坏的——终点从此落在 0.8 而不是 1.0，而
    /// <c>CubicEaseOut</c> 在 0.8 处已经走完了 99%（<c>1-0.2³</c>），
    /// 于是整段减速被挤进前 41% 的时间里，后面一大截是干等，
    /// 2026-09-15 主人报的「csgo 减速过程消失了」就是这个。
    /// 定格挪到动画之外以后，缓动曲线又能铺满整段动作了。
    /// <para/>
    /// 声明成 <c>protected internal</c>：子类要用它，测试要看它。
    /// </remarks>
    protected internal TimeSpan Settle { get; private set; }

    /// <summary>装载排片表。必须在 <see cref="PlayAsync"/> 之前调用。</summary>
    public void Load(RevealAnimationPlan plan)
    {
        // 定格时长从排片表抄下来。喵~防御：排片表是空的（调用方写错）时按「不定格」处理。
        Settle = plan?.Settle ?? TimeSpan.Zero;
        // 具体怎么摆，交给各个样式自己决定。
        LoadPlan(plan);
    }

    /// <summary>各个样式自己的装载逻辑。</summary>
    /// <remarks>
    /// 拆成两级而不是让各个样式直接覆写 <see cref="Load"/>，是为了让「抄定格时长」
    /// 这件事只有一处写法——四个样式各写一遍的话，漏掉哪个哪个就不定格，
    /// 而这个毛病只表现为「最后一帧被结果顶掉」，不会报错。
    /// </remarks>
    protected abstract void LoadPlan(RevealAnimationPlan plan);

    /// <summary>
    /// 按排片表造出这段动画要播的一个或几个 <see cref="Animation"/>，<b>按播放顺序</b>排好。
    /// </summary>
    /// <remarks>
    /// 大多样式只有一个；转盘是两个（先转到缝上、再滑进格子）。
    /// 装载失败或数据不满足这个样式时返回空集合——上层照样会走「出结果」的收尾。
    /// <para/>
    /// 单独抽出来而不是直接写在播放里，是为了让「时长和时刻表对不对得上」能被单测验到：
    /// 缺 <c>Duration</c>、关键帧越过总时长这些毛病，都只能在造出来的这个对象上验。
    /// </remarks>
    internal abstract IReadOnlyList<Animation> BuildAnimations();

    /// <summary>
    /// 播放这段动画，播完再原地静止 <see cref="Settle"/> 那么久。被取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <remarks>
    /// 多段必须<b>依次</b>播完再放下一个，不能一起起播——转盘那两段写的是同一个角度属性，
    /// 同时跑会互相盖掉，看起来就是「转到一半突然跳过去」。
    /// <para/>
    /// 定格接在动作之后：每一段都播完了，进度停在终点，画面就是静止的，
    /// 所以这里只要等一会儿就好，不需要再驱动任何属性。
    /// 取消令牌一路带下去，主人点「跳」或者窗口要关时这里会立刻退出。
    /// </remarks>
    public async Task PlayAsync(CancellationToken token)
    {
        foreach (var animation in BuildAnimations())
        {
            await animation.RunAsync(this, token);
        }

        // 动作演完，画面停在终点上不动，让主人看清结果。
        // 喵~防御：排片表说不用定格（时长为 0 或负数）时直接跳过，别白等一个零。
        if (Settle > TimeSpan.Zero)
        {
            await Task.Delay(Settle, token);
        }
    }

    /// <summary>把进度硬置到终点。</summary>
    /// <remarks>
    /// 只在动画<b>正常播完</b>时调用。被取消时绝不能调用——控件会被下一轮复用，
    /// 硬置的终值会让它一上来就显示上一轮的中选者。
    /// </remarks>
    public abstract void SnapToEnd();

    /// <summary>按舞台尺寸申请空间，别让布局量出 0×0。</summary>
    protected override Size MeasureOverride(Size availableSize) => StageSize;

    /// <summary>按指定字号排一段文字。</summary>
    protected FormattedText MeasureText(string text, double fontSize, IBrush brush) =>
        new(text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            // 半粗体，和中央大字的字重保持一致。
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            fontSize,
            brush);

    /// <summary>
    /// 在指定区域里居中画一段文字，放不下就按比例缩小字号。
    /// </summary>
    /// <param name="context">绘制上下文。</param>
    /// <param name="text">要画的文字。</param>
    /// <param name="area">文字可以占用的矩形区域。</param>
    /// <param name="fontSize">期望的字号。</param>
    /// <param name="brush">文字颜色。</param>
    protected void DrawFittedText(DrawingContext context, string text, Rect area,
        double fontSize, IBrush brush)
    {
        // 喵~防御：区域还没量出来（宽或高是 0、负数、NaN）时画不了，直接跳过。
        if (!double.IsFinite(area.Width) || !double.IsFinite(area.Height)
            || area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        // 喵~防御：名字理论上不会是空的，但名单是外部文件读进来的，空串直接跳过。
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = MeasureText(text, fontSize, brush);
        // 宽度超了就把字号按比例缩回去，最多缩到一半——再小就真看不清了。
        if (formatted.Width > area.Width)
        {
            var shrink = Math.Max(0.5, area.Width / formatted.Width);
            formatted = MeasureText(text, fontSize * shrink, brush);
        }

        // 横竖都居中摆。
        context.DrawText(formatted, new Point(
            area.X + (area.Width - formatted.Width) / 2,
            area.Y + (area.Height - formatted.Height) / 2));
    }

    /// <summary>画一个圆角方块。动画里的「格子」「名字方块」都用它。</summary>
    protected static void DrawSlot(DrawingContext context, Rect rect, IBrush background,
        IBrush border, double borderThickness, double cornerRadius) =>
        context.DrawRectangle(background, new Pen(border, borderThickness), rect, cornerRadius, cornerRadius);
}
