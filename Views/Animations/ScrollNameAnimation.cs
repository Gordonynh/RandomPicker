using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Views.Animations;

/// <summary>
/// 「滚动名字」动画：中央大字不停地换名字，换得越来越慢，最后定格在中选者上。
/// </summary>
/// <remarks>
/// 驱动属性是 <see cref="Progress"/>，单位是<b>帧号</b>而不是 0~1 的比例。
/// 排片表给出的换帧间隔是<b>等比增长</b>的（越换越慢），两条关键帧之间线性插值，
/// 所以 <c>floor(Progress)</c> 天然就是「现在停在第一帧」，
/// 小数部分则是「正从这一帧滚到下一帧、滚了多少」。
/// <para/>
/// 用帧号当进度还有一层好处：帧数由排片表决定，界面上不需要再知道任何时刻表。
/// </remarks>
internal sealed class ScrollNameAnimation : RevealAnimationBase
{
    /// <summary>当前进度，单位是帧号（0 表示第一帧）。</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<ScrollNameAnimation, double>(nameof(Progress));

    static ScrollNameAnimation()
    {
        // 进度一变就重画。
        AffectsRender<ScrollNameAnimation>(ProgressProperty);
    }

    /// <summary>依次显示的名字，最后一帧必定是中选者。</summary>
    private IReadOnlyList<string> _frames = [];

    /// <summary>每一帧停留多久，单位：秒，长度和 <see cref="_frames"/> 一致。</summary>
    private IReadOnlyList<double> _intervals = [];

    /// <summary>一行文字占多高。滚动时上下两行就是这个间距。</summary>
    private double _lineHeight;

    /// <summary>
    /// 动作停下的时刻。末帧落在这儿，之后到排片表的总时长为止是定格。
    /// </summary>
    /// <remarks>
    /// <b>它同时也是这段动画的 <c>Duration</c>。</b>定格不走动画时长，
    /// 而是由 <see cref="RevealAnimationBase.PlayAsync"/> 在播完之后原地等一会儿——
    /// 把定格算进 <c>Duration</c> 会让关键帧的 cue 整体缩短，缓动曲线就走不满了。
    /// </remarks>
    private TimeSpan _motionTotal;

    /// <summary>算好的舞台尺寸。</summary>
    private Size _stageSize;

    public ScrollNameAnimation(double revealFontSize, Color accent)
        : base(revealFontSize, accent)
    {
        // 一行的高度留出 35% 的行距，换名字时才不会挤在一起。
        _lineHeight = RevealFontSize * 1.35;
    }

    /// <summary>当前进度，单位是帧号。</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>宽度放下最长的一个名字，高度放下上下两行——滚动才有地方走。</summary>
    public override Size StageSize => _stageSize;

    /// <inheritdoc/>
    protected override void LoadPlan(RevealAnimationPlan plan)
    {
        // 喵~防御：装错了计划类型（调用方写错）时什么都不画，也好过抛异常把结果卡住。
        if (plan is not ScrollPlan scroll)
        {
            _frames = [];
            _intervals = [];
            _motionTotal = TimeSpan.Zero;
            _stageSize = default;
            return;
        }

        _frames = scroll.Frames;
        _intervals = scroll.Intervals;
        // 动作时长就是排片表的 MotionTotal，末帧落在那一刻；定格由基类在播完之后等。
        _motionTotal = scroll.MotionTotal;
        _lineHeight = RevealFontSize * 1.35;

        // 舞台宽度要放得下最长的那一帧，不然换到长名字时会被裁掉。
        var widest = 0.0;
        foreach (var frame in _frames)
        {
            // 按最终字号量一遍宽度。
            var width = MeasureText(frame, RevealFontSize, Brushes.White).Width;
            if (width > widest)
            {
                widest = width;
            }
        }

        // 至少留出两个字加两侧留白的宽度，名单里全是单字名时才不会窄成一条。
        var stageWidth = Math.Max(RevealFontSize * 2.4, widest + RevealFontSize * 0.8);
        // 高度是上下两行，正好容下滚动过程中同时出现的那两帧。
        _stageSize = new Size(stageWidth, _lineHeight * 2);
    }


    /// <summary>
    /// 按排片表造出这段动画。抽出来是为了让「时长和时刻表对不对得上」能被单测验到。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Animation.Duration"/> 必须写，漏了整段动画直接不播。</b>
    /// Avalonia 用它当分母把每个关键帧的 <c>KeyTime</c> 换算成 cue
    /// （<c>Animation.cs</c> 里 <c>cue = KeyTime.TotalSeconds / Duration.TotalSeconds</c>）。
    /// 默认值是 <see cref="TimeSpan.Zero"/>，于是第一个关键帧算出 <c>0/0 = NaN</c>，
    /// 而 <c>Cue</c> 的构造函数只接受 0~1、NaN 两个比较都不成立，
    /// 当场抛「This cue object's value should be within or equal to 0.0 and 1.0」。
    /// 那个异常会被上层「绝不吞掉结果」的兜底接住，表现是<b>结果照出、动画全无、还不报错</b>——
    /// 2026-09-15 主人报的「动画并没有触发」就是这个。
    /// <para/>
    /// 时长就是排片表的 <c>MotionTotal</c>（动作时长），末帧正好落在这一刻上，
    /// 缓动曲线因此铺满整段动作。定格不在这里：基类在播完之后原地等一会儿，
    /// 那时进度停在终点、画面静止，中选者的名字就这么停着让人看清。
    /// </remarks>
    internal override IReadOnlyList<Animation> BuildAnimations()
    {
        // 帧间线性插值，减速效果全部由「间隔越来越长」来表达。
        var animation = new Animation
        {
            // 动画时长就是动作时长。定格不算在这里，播完由基类原地等一会儿。
            Duration = _motionTotal,
            FillMode = FillMode.Forward,
            Easing = new LinearEasing()
        };

        var elapsed = TimeSpan.Zero;
        for (var i = 0; i < _frames.Count; i++)
        {
            // 第 i 帧停留的时长；坏值当 0 处理，不至于把整段时间轴带偏。
            var seconds = double.IsFinite(_intervals[i]) && _intervals[i] > 0 ? _intervals[i] : 0;
            // 这一帧从「当前已经累加到的时刻」开始显示——<b>先取值，再把这一帧的时长加进去</b>。
            // 顺序反过来的话，每一帧占用的都是下一帧的时长：中选者那一格本该停留最久
            // （等比数列的末项，整段里最长的一段），却会被倒数第二个名字占掉，
            // 他自己一出现就进定格——2026-09-15 主人说的「他最后变慢的过程似乎消失了」。
            var at = elapsed;
            // 喵~防御：坏数据让累加值越过动作时长时，从这里起不再放关键帧。
            // cue 越过 1 会让 Avalonia 的 Cue 构造函数当场抛异常，整段动画就没了，
            // 而症状只是「动画没播」——这个坑踩过一次了。
            if (at >= _motionTotal)
            {
                break;
            }

            // 累加这一帧的时长，给下一帧当时刻用。
            elapsed += TimeSpan.FromSeconds(seconds);
            // 关键帧落在这个时刻，值就是帧号 i。
            // 第 0 帧因此正好落在时刻 0 上，不需要另加一个起点帧
            // ——那会造出两个 cue 相同的帧，Avalonia 算插值时拿它俩当区间会除以零。
            animation.Children.Add(new KeyFrame
            {
                KeyTime = at,
                Setters = { new Setter(ProgressProperty, (double)i) }
            });
        }

        return [animation];
    }

    /// <inheritdoc/>
    public override void SnapToEnd() => Progress = Math.Max(0, _frames.Count - 1);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 喵~防御：尺寸还没量出来（0×0）或者没有帧时画不了，直接返回。
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _frames.Count == 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;

        // 喵~防御：属性还没被动画写过时可能是 NaN，按第 0 帧处理。
        var raw = double.IsFinite(Progress) ? Progress : 0;
        // 整数部分就是当前帧号。
        var index = Math.Clamp((int)Math.Floor(raw), 0, _frames.Count - 1);
        // 小数部分是「正滚到下一帧、滚了多少」。
        var fraction = Math.Clamp(raw - index, 0, 1);
        // 下一帧；已经是最后一帧时就是它自己。
        var nextIndex = Math.Min(index + 1, _frames.Count - 1);

        // 上一帧往上滚出去时的纵向位置。
        var outgoingTop = height / 2 - _lineHeight / 2 - fraction * _lineHeight;
        // 下一帧从下面滚进来时的纵向位置。
        var incomingTop = height / 2 - _lineHeight / 2 + (1 - fraction) * _lineHeight;

        // 滚出去的那一帧越淡、滚进来的越清楚，两行字才不会糊成一团。
        var outgoingBrush = new SolidColorBrush(Colors.White, 1 - 0.62 * fraction);
        // 滚进来的那一帧的透明度与上一条相反。
        var incomingBrush = new SolidColorBrush(Colors.White, 0.38 + 0.62 * fraction);

        // 定格之后（两帧是同一个）只画一行，不重复画两遍。
        if (nextIndex == index)
        {
            DrawFittedText(context, _frames[index],
                new Rect(0, height / 2 - _lineHeight / 2, width, _lineHeight), RevealFontSize, Brushes.White);
            return;
        }

        // 先画滚出去的那一帧。
        DrawFittedText(context, _frames[index], new Rect(0, outgoingTop, width, _lineHeight),
            RevealFontSize, outgoingBrush);
        // 再画滚进来的那一帧，盖在上面。
        DrawFittedText(context, _frames[nextIndex], new Rect(0, incomingTop, width, _lineHeight),
            RevealFontSize, incomingBrush);
    }
}
