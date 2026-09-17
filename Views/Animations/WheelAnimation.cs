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
/// 「拼多多转盘」动画：指针先空转几圈减速停在两格之间的缝上，再往左或往右挪半格滑进中选那一格。
/// </summary>
/// <remarks>
/// 驱动属性是 <see cref="Angle"/>，单位是<b>度</b>——轮盘本身转了多少（顺时针为正）。
/// 角度和「指针指着第几格」的换算完全交给 <see cref="WheelLayout.SectorUnderPointer"/>，
/// 界面上不另写一套判定：算错了会指着一个不是中选者的格子停下来，而且只在部分扇区数下出错，
/// 极难复现，所以判定只能有一个出处。
/// </remarks>
internal sealed class WheelAnimation : RevealAnimationBase
{
    /// <summary>轮盘转了多少度（顺时针为正）。</summary>
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<WheelAnimation, double>(nameof(Angle));

    static WheelAnimation()
    {
        // 角度一变就重画。
        AffectsRender<WheelAnimation>(AngleProperty);
    }

    /// <summary>盘面上的名字，顺时针排列。</summary>
    private IReadOnlyList<string> _sectors = [];

    /// <summary>停到那条缝上时的角度。第一段的终点。</summary>
    private double _boundaryAngle;

    /// <summary>
    /// 最终（滑进中选格）的角度。第二段的终点也是它，<see cref="SnapToEnd"/> 也落在这里。
    /// </summary>
    /// <remarks>
    /// 盘面上哪一格是中选者不再单独记：高亮跟着指针走，停稳时指针指着的自然就是他。
    /// </remarks>
    private double _finalAngle;

    /// <summary>转到缝上用多久。</summary>
    private TimeSpan _holdDuration = TimeSpan.FromSeconds(6);

    /// <summary>从缝滑进格子里用多久。它同时也是第二段动画的时长。</summary>
    private TimeSpan _slideDuration = TimeSpan.FromSeconds(0.35);

    /// <summary>转盘半径。</summary>
    private double _radius;

    /// <summary>算好的舞台尺寸。</summary>
    private Size _stageSize;

    public WheelAnimation(double revealFontSize, Color accent)
        : base(revealFontSize, accent)
    {
    }

    /// <summary>轮盘当前转了多少度。</summary>
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    /// <summary>一个正方形：轮盘外圈，再加上顶部指针要占的那点地方。</summary>
    public override Size StageSize => _stageSize;

    /// <summary>
    /// 此刻指针指着的那一格，也就是盘面上被点亮的那一格。
    /// </summary>
    /// <remarks>
    /// 渲染只用这一个出处来决定谁亮，测试也验它——「高亮必须永远等于指针的位置」
    /// 就是防剧透的全部内容。这里的判定复用 <see cref="WheelLayout.SectorUnderPointer"/>，
    /// 和「指针最后停在中选者身上」那条用的是同一个函数，两边不可能算出不一样的结果。
    /// </remarks>
    internal int HighlightedSectorIndex =>
        // 喵~防御：属性还没被动画写过时可能是 NaN，按「还没开始转」处理。
        WheelLayout.SectorUnderPointer(double.IsFinite(Angle) ? Angle : 0, _sectors.Count);

    /// <inheritdoc/>
    protected override void LoadPlan(RevealAnimationPlan plan)
    {
        // 喵~防御：装错了计划类型或者盘面不足两格时什么都不画，也好过抛异常把结果卡住。
        if (plan is not WheelPlan wheel || wheel.Sectors.Count < 2)
        {
            _sectors = [];
            _stageSize = default;
            return;
        }

        _sectors = wheel.Sectors;
        _boundaryAngle = wheel.BoundaryAngle;
        _finalAngle = wheel.FinalAngle;
        _holdDuration = wheel.HoldDuration;
        // 第二段的时长就是「滑半格」的时间，两段加起来正好是动作时长；定格由基类在播完之后等。
        _slideDuration = wheel.SlideDuration;

        // 半径按字号档位换算，保证和插件其它部分同一套视觉尺度。
        _radius = RevealFontSize * 2.2;
        // 舞台是正方形：直径加上顶部指针和一圈描边的余量。
        var side = (_radius + RevealFontSize * 0.6) * 2;
        _stageSize = new Size(side, side);
    }


    /// <summary>
    /// 按排片表造出这两段动画。抽出来是为了让「两段合起来正好是动作时长」能被单测验到。
    /// </summary>
    /// <remarks>
    /// 第一段转到缝上、第二段滑进中选格，两段的 <c>Duration</c> 加起来正好是排片表的动作时长，
    /// 各自的缓动曲线因此都铺满自己那一段。
    /// 定格不在这里：基类在播完之后原地等一会儿，那时指针停在格子上不动，让人看清停在哪一位。
    /// </remarks>
    internal override IReadOnlyList<Animation> BuildAnimations()
    {
        // 喵~防御：盘面不足两格（装载失败）时一段都不播，上层照样会出结果。
        if (_sectors.Count < 2)
        {
            return [];
        }

        // 第一段：空转几圈，减速停在两格之间的缝上。用强减速缓动，停之前有「刹住」的感觉。
        var spin = new Animation
        {
            Duration = _holdDuration,
            FillMode = FillMode.Forward,
            Easing = RevealEasing.Decelerate
        };
        spin.Children.Add(new KeyFrame
        {
            KeyTime = TimeSpan.Zero,
            Setters = { new Setter(AngleProperty, 0.0) }
        });
        spin.Children.Add(new KeyFrame
        {
            KeyTime = _holdDuration,
            Setters = { new Setter(AngleProperty, _boundaryAngle) }
        });

        // 第二段的时长就是滑半格的时间。两段加起来 = 排片表的动作时长，
        // 缓动曲线各自铺满自己那一段；定格由基类在播完之后等。
        var slideLength = _slideDuration;
        // 喵~防御：排片表被改坏、滑入时长不是正数时，退回默认那半格的时间——
        // 时长是 0 的话 Avalonia 算 cue 会得到 0/0，当场抛异常，整段动画就没了。
        if (slideLength <= TimeSpan.Zero)
        {
            slideLength = TimeSpan.FromSeconds(0.35);
        }

        // 第二段：从缝上挪半格滑进中选那一格。带一点回弹，像被指针「吸」进去。
        var slide = new Animation
        {
            Duration = slideLength,
            FillMode = FillMode.Forward,
            Easing = RevealEasing.Settle
        };
        slide.Children.Add(new KeyFrame
        {
            KeyTime = TimeSpan.Zero,
            Setters = { new Setter(AngleProperty, _boundaryAngle) }
        });
        slide.Children.Add(new KeyFrame
        {
            KeyTime = _slideDuration,
            Setters = { new Setter(AngleProperty, _finalAngle) }
        });

        return [spin, slide];
    }

    /// <inheritdoc/>
    public override void SnapToEnd() => Angle = _finalAngle;

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 喵~防御：尺寸还没量出来（0×0）或者盘面不足两格时画不了，直接返回。
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _sectors.Count < 2 || _radius <= 0)
        {
            return;
        }

        // 圆心。舞台是正方形，正中间就是圆心。
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // 喵~防御：属性还没被动画写过时可能是 NaN，按「还没开始转」处理。
        var angle = double.IsFinite(Angle) ? Angle : 0;
        // 每一格占多少度。
        var sweep = 360.0 / _sectors.Count;
        // 指针此刻指着的那一格。<b>高亮就跟着它走，而不是预先点亮中选者那一格。</b>
        // 预先点亮会剧透：第二段的滑动方向是两种之一，其中一种会让指针先落在中选者旁边那一格上，
        // 再滑进中选者——那段时间里指针还没到，中选者却已经亮着了，等于提前揭晓。
        // 跟着指针走就没有这个问题：高亮的位置永远等于指针的位置，看不出任何额外信息，
        // 而且转起来的时候像有一道光跟着指针扫过盘面。2026-09-15 主人提的改进。
        var pointerSector = HighlightedSectorIndex;
        // 文字横向能占多宽：扇形中线上那一段弦长，取九成，免得字压到相邻格子上。
        var labelRadius = _radius * 0.64;
        var maxTextWidth = 2 * labelRadius * Math.Sin(sweep / 2 * Math.PI / 180) * 0.9;
        // 一行文字占的高度。
        var lineHeight = RevealFontSize * 0.62;

        for (var i = 0; i < _sectors.Count; i++)
        {
            // 这一格在屏幕上占的角度区间；轮盘转了 angle 度，区间整体跟着挪。
            var start = WheelLayout.SectorStart(i, _sectors.Count) + angle;
            var end = start + sweep;
            // 这一格的扇形几何。
            var geometry = BuildSector(center, _radius, start, end);
            // 指针指着的那一格就点亮。停稳时指针正好停在中选者身上，所以点亮的那格必然是他。
            var isHighlighted = i == pointerSector;

            // 底色深浅交替，相邻两格才分得开。
            var fill = isHighlighted
                ? new SolidColorBrush(Accent, 0.38)
                : new SolidColorBrush(i % 2 == 0
                    ? Color.FromRgb(0x24, 0x24, 0x2C)
                    : Color.FromRgb(0x1B, 0x1B, 0x22));
            // 描边：指针指着的那一格亮一些。
            var border = isHighlighted
                ? new SolidColorBrush(Accent, 0.9)
                : new SolidColorBrush(Colors.White, 0.16);

            context.DrawGeometry(fill, new Pen(border, 1.2), geometry);

            // 这一格的中线方向，名字就摆在这条线上。
            var middle = start + sweep / 2;
            // 名字的中心点。
            var labelCenter = PointAt(center, labelRadius, middle);
            // 名字占用的矩形：横向按弦长限制，纵向是一行高。
            var labelArea = new Rect(labelCenter.X - maxTextWidth / 2,
                labelCenter.Y - lineHeight / 2, maxTextWidth, lineHeight);
            // 指针指着的那一格用强调色，其余是白的。
            IBrush textBrush = isHighlighted ? new SolidColorBrush(Accent) : Brushes.White;
            // 名字横向摆正画出来就行——扇形排成一圈，横排永远读得正。
            DrawFittedText(context, _sectors[i], labelArea, lineHeight * 0.82, textBrush);
        }

        // 圆心的小圆盖，把扇形收在一起的那一点遮掉。
        context.DrawEllipse(new SolidColorBrush(Accent, 0.85), null, center,
            RevealFontSize * 0.13, RevealFontSize * 0.13);
        // 指针画在最上层，永远盖住盘面。
        DrawPointer(context, center);
    }

    /// <summary>画正上方那个固定不动的指针。</summary>
    private void DrawPointer(DrawingContext context, Point center)
    {
        // 三角的底边半宽。
        var size = RevealFontSize * 0.24;
        // 三角顶点，压在轮盘外圈里面一点。
        var tip = new Point(center.X, center.Y - _radius + RevealFontSize * 0.12);
        // 三角底边所在的纵坐标。
        var baseY = center.Y - _radius - RevealFontSize * 0.42;

        var marker = new StreamGeometry();
        using (var geometry = marker.Open())
        {
            // 从底边左端出发，连到右端，再收到顶点——一个朝下的三角。
            geometry.BeginFigure(new Point(center.X - size, baseY), true);
            geometry.LineTo(new Point(center.X + size, baseY));
            geometry.LineTo(tip);
            geometry.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(Accent), null, marker);
    }

    /// <summary>围出一块扇形。</summary>
    /// <param name="center">圆心。</param>
    /// <param name="radius">半径。</param>
    /// <param name="startDegrees">起始角，0 度是正上方。</param>
    /// <param name="endDegrees">结束角，必定大于起始角。</param>
    private static Geometry BuildSector(Point center, double radius, double startDegrees, double endDegrees)
    {
        // 弧的两个端点。
        var start = PointAt(center, radius, startDegrees);
        var end = PointAt(center, radius, endDegrees);

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            // 从圆心出发，连到起始角那一点。
            path.BeginFigure(center, true);
            path.LineTo(start);
            // 沿外圈顺时针走到结束角。跨过半圈时必须置 IsLargeArc，
            // 否则弧会往短的那一边画，扇区就反了；正好半圈（两格）时置 false 才是对的。
            path.ArcTo(end, new Size(radius, radius), 0,
                endDegrees - startDegrees > 180, SweepDirection.Clockwise);
            // 闭合回圆心，围成一块扇形。
            path.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>离圆心 <paramref name="radius"/>、方向是「正上方顺时针 <paramref name="degrees"/> 度」的那个点。</summary>
    private static Point PointAt(Point center, double radius, double degrees)
    {
        // 换成弧度。
        var radians = degrees * Math.PI / 180.0;
        // 屏幕坐标系 y 向下，所以「正上方」是减 y；角度增大往右偏，就是顺时针。
        return new Point(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
    }
}
