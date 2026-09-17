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
/// 「CSGO 开箱」动画：一排名字方块横向滚过，指针停在中间那一块上。
/// </summary>
/// <remarks>
/// 驱动属性是 <see cref="Offset"/>，单位是<b>像素</b>——整条轨道往左平移多少。
/// 方块该画在哪儿、指针底下是第几块，全部由它现算，和 <see cref="CsgoTrack"/> 里的几何是同一套公式：
/// 排片表给出的 <c>TargetOffset</c> 保证滚到终点时指针下的方块<b>必然</b>是中选者。
/// </remarks>
internal sealed class CsgoAnimation : RevealAnimationBase
{
    /// <summary>轨道往左平移了多少像素。</summary>
    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<CsgoAnimation, double>(nameof(Offset));

    static CsgoAnimation()
    {
        // 平移量一变就重画。
        AffectsRender<CsgoAnimation>(OffsetProperty);
    }

    /// <summary>轨道上的名字序列（名单重复展开若干轮）。</summary>
    private IReadOnlyList<string> _track = [];

    /// <summary>每个方块对应的品质，下标和 <see cref="_track"/> 一一对应。</summary>
    private IReadOnlyList<ItemRarity> _rarities = [];

    /// <summary>中选者的品质。停稳之后要用它给中选那块上色。</summary>
    private ItemRarity _winnerRarity = ItemRarity.MilSpec;

    /// <summary>一个方块的宽度，单位：逻辑像素。</summary>
    private double _slotWidth;

    /// <summary>一个方块的高度。</summary>
    private double _slotHeight;

    /// <summary>方块之间的间距。</summary>
    private double _gap;

    /// <summary>滚到终点时的平移量。用来判断「停稳了没有」。</summary>
    private double _targetOffset;

    /// <summary>
    /// 动作停下的时刻。滚到这里的瞬间就定格了。
    /// </summary>
    /// <remarks>
    /// <b>它同时也是这段动画的 <c>Duration</c>。</b>定格不走动画时长，
    /// 而是由 <see cref="RevealAnimationBase.PlayAsync"/> 在播完之后原地等一会儿——
    /// 把定格算进 <c>Duration</c> 会让终点关键帧的 cue 从 1.0 掉到 0.8，
    /// 而减速曲线在 0.8 处已经走完了 99%，整段减速会被挤进前 41% 的时间里，
    /// 观感就是「猛地冲过去然后干等」。2026-09-15 主人报的「减速过程消失了」就是这个。
    /// </remarks>
    private TimeSpan _motionTotal = TimeSpan.FromSeconds(4);

    /// <summary>算好的舞台尺寸，等于排片表给的可视区大小。</summary>
    private Size _stageSize;

    public CsgoAnimation(double revealFontSize, Color accent)
        : base(revealFontSize, accent)
    {
    }

    /// <summary>轨道当前往左平移了多少像素。</summary>
    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    /// <summary>可视区大小。</summary>
    public override Size StageSize => _stageSize;

    /// <inheritdoc/>
    protected override void LoadPlan(RevealAnimationPlan plan)
    {
        // 喵~防御：装错了计划类型时什么都不画，也好过抛异常把结果卡住。
        if (plan is not CsgoPlan csgo)
        {
            _track = [];
            _rarities = [];
            _stageSize = default;
            return;
        }

        _track = csgo.Track;
        _rarities = csgo.Rarities;
        _winnerRarity = csgo.WinnerRarity;
        _slotWidth = csgo.SlotWidth;
        _slotHeight = csgo.SlotHeight;
        _gap = csgo.Gap;
        _targetOffset = csgo.TargetOffset;
        // 动作时长就是排片表的 MotionTotal；定格由基类在播完之后等。
        _motionTotal = csgo.MotionTotal;
        // 可视区多大，舞台就多大——平移量本来就是按这个宽度算出来的，两者必须一致。
        _stageSize = new Size(csgo.ViewportWidth, csgo.ViewportHeight);
    }


    /// <summary>
    /// 按排片表造出这段动画。抽出来是为了让「时长和时刻表对不对得上」能被单测验到。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Animation.Duration"/> 必须写</b>：Avalonia 拿它当分母把 <c>KeyTime</c>
    /// 换算成 cue，漏了会算出 <c>0/0</c> 并当场抛异常（见 <see cref="ScrollNameAnimation.BuildAnimations"/> 的说明）。
    /// <para/>
    /// 终点落在动作时长上，也就是 cue = 1.0：<b>减速曲线因此铺满整段</b>。
    /// 定格不在这里——基类在播完之后原地等一会儿，那时方块已经停在指针下、高亮着不动。
    /// </remarks>
    internal override IReadOnlyList<Animation> BuildAnimations()
    {
        // 从静止开始，一路加速冲过去再减速停住。
        var animation = new Animation
        {
            // 动画时长就是动作时长，缓动曲线铺满全程。
            Duration = _motionTotal,
            FillMode = FillMode.Forward,
            // 共用减速曲线：起手快、收尾一点点挪，才有「刹住」的手感。
            Easing = RevealEasing.Decelerate
        };

        // 起点：还没开始滚。
        animation.Children.Add(new KeyFrame
        {
            KeyTime = TimeSpan.Zero,
            Setters = { new Setter(OffsetProperty, 0.0) }
        });

        // 终点：中选者那一块压在指针附近。落点在动作时长的末尾，cue 因此正好是 1。
        animation.Children.Add(new KeyFrame
        {
            KeyTime = _motionTotal,
            Setters = { new Setter(OffsetProperty, _targetOffset) }
        });

        return [animation];
    }

    /// <inheritdoc/>
    public override void SnapToEnd() => Offset = _targetOffset;

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 喵~防御：尺寸还没量出来（0×0）或者轨道是空的时画不了，直接返回。
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _track.Count == 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;

        // 喵~防御：属性还没被动画写过时可能是 NaN，按「还没开始滚」处理。
        var offset = double.IsFinite(Offset) ? Offset : 0;
        // 相邻两块中心的距离。宽度至少是 1，不会除零。
        var pitch = _slotWidth + _gap;
        // 停稳了没有：滚到终点就说明中选者已经在指针下面了。
        var settled = Math.Abs(offset - _targetOffset) < 0.5;
        // 方块纵向居中摆。
        var slotTop = (height - _slotHeight) / 2;
        // 指针（固定在正中央）底下是第几块。几何判定完全交给 CsgoTrack，和排片表同源。
        var centerIndex = CsgoTrack.IndexAtOffset(offset, _slotWidth, _gap, width);

        for (var i = 0; i < _track.Count; i++)
        {
            // 这一块左边缘在屏幕上的位置。
            var left = i * pitch - offset;

            // 完全在可视区外面的方块直接跳过——名单展开好几轮时能省下大量绘制。
            if (left + _slotWidth < 0 || left > width)
            {
                continue;
            }

            var rect = new Rect(left, slotTop, _slotWidth, _slotHeight);
            // 是不是指针正下方那一块。
            var isCenter = i == centerIndex;
            // 离中心越远越淡，视线自然被拉到指针附近。
            var distance = Math.Min(1, Math.Abs(rect.Center.X - width / 2) / (width / 2));
            // 指针下那一块始终满亮，其余按距离衰减到四成。
            var opacity = isCenter ? 1.0 : 0.4 + 0.6 * (1 - distance);

            // 中选那一块，而且已经停稳：这是「开出来的东西」，要最亮。
            var revealed = isCenter && settled;
            // 这一块要显示哪个品质：中选那块用真正摇出来的，
            // 其余用轨道上各自那份装饰值。喵~防御：rarities 理论上和 track 一样长，
            // 长度对不上时（计划被改坏）退回最常见那一档，而不是越界。
            var rarity = revealed
                ? _winnerRarity
                : (i < _rarities.Count ? _rarities[i] : ItemRarity.MilSpec);
            var rarityColor = RarityColors.ColorOf(rarity);

            // 底色：暗底往品质色上偏。停稳的中选块偏得最多，像被点亮了。
            // 2026-09-16 主人要求「品质色改明显明亮一点」，偏向比例因此从三成提到六成：
            // 原来五个方块并排几乎都是一样的深灰，扫一眼认不出哪块是什么品质。
            // 提亮底色不影响读名字——文字是白的、描边是品质色，都压得住。
            var tint = revealed ? 0.62 : 0.30;
            var background = new SolidColorBrush(
                RarityColors.Mix(Color.FromRgb(0x2A, 0x2A, 0x34), rarityColor, tint));
            // 描边用品质色：停稳后实心描边，滚动中按离指针的距离淡一层。
            IBrush border = revealed
                ? new SolidColorBrush(rarityColor)
                : new SolidColorBrush(rarityColor, isCenter ? 0.85 : 0.52);
            // 文字：停稳后直接用品质色，其余保持白色。
            IBrush textBrush = revealed ? new SolidColorBrush(rarityColor) : Brushes.White;

            using (context.PushOpacity(opacity))
            {
                DrawSlot(context, rect, background, border, revealed ? 2.5 : 1.2, _slotHeight * 0.16);
                // 名字画在方块里，放不下会自动缩字号。
                DrawFittedText(context, _track[i],
                    rect.Deflate(new Thickness(_slotWidth * 0.08, _slotHeight * 0.12)),
                    _slotHeight * 0.42, textBrush);
            }
        }

        // 指针：方块区域上下各一个三角，顶点朝着方块，指哪停哪。
        DrawPointer(context, width, slotTop);
    }

    /// <summary>画正中央的指针。</summary>
    private void DrawPointer(DrawingContext context, double width, double slotTop)
    {
        var centerX = width / 2;
        // 三角的高。
        var size = _slotHeight * 0.16;
        var brush = new SolidColorBrush(Accent);

        // 方块上边缘往上一点点，画一条短竖线，让指针从画面外「伸」进来。
        context.DrawLine(new Pen(brush, 2),
            new Point(centerX, slotTop - size * 2.2), new Point(centerX, slotTop));
        // 方块下边缘往下，同样伸出去一小段。
        context.DrawLine(new Pen(brush, 2),
            new Point(centerX, slotTop + _slotHeight), new Point(centerX, slotTop + _slotHeight + size * 2.2));

        // 两个三角合成一个几何体，一次画完。
        var marker = new StreamGeometry();
        using (var geometry = marker.Open())
        {
            // 上面的三角：顶点朝下，指着方块的上边缘。
            geometry.BeginFigure(new Point(centerX - size, slotTop - size * 2.2), true);
            geometry.LineTo(new Point(centerX + size, slotTop - size * 2.2));
            geometry.LineTo(new Point(centerX, slotTop));
            geometry.EndFigure(true);

            // 下面的三角：顶点朝上，指着方块的下边缘。
            geometry.BeginFigure(new Point(centerX - size, slotTop + _slotHeight + size * 2.2), true);
            geometry.LineTo(new Point(centerX + size, slotTop + _slotHeight + size * 2.2));
            geometry.LineTo(new Point(centerX, slotTop + _slotHeight));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(brush, null, marker);
    }
}
