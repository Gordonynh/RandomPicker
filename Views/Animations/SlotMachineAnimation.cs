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
/// 「老虎机」动画：一排格子，一格一格地抽字，每格停在有可能的那个字上。
/// </summary>
/// <remarks>
/// 驱动属性是四个 <c>Slot0Progress</c>~<c>Slot3Progress</c>，各占 0~1，
/// 分别表示「这一格转了多少」。同一时刻只有一格在转，所以拼起来就是一个完整的抽字过程。
/// <para/>
/// <b>最容易被写坏的地方：</b>中选者比格数短的时候（四字名的名单里抽到「张三」），
/// 末尾几格要留空。<see cref="SlotPlan.WinnerChars"/> 里对应项是 <c>null</c>，
/// 这一格<b>从头到尾都画空框、一个字都不转</b>——绝不能拿名单里别人的字去填，
/// 那样板面和真正中选的人对不上，是最伤信任的一类 bug。
/// </remarks>
internal sealed class SlotMachineAnimation : RevealAnimationBase
{
    /// <summary>第一格的进度：这一格转了多少（0~1）。</summary>
    public static readonly StyledProperty<double> Slot0ProgressProperty =
        AvaloniaProperty.Register<SlotMachineAnimation, double>(nameof(Slot0Progress));

    /// <summary>第二格的进度。</summary>
    public static readonly StyledProperty<double> Slot1ProgressProperty =
        AvaloniaProperty.Register<SlotMachineAnimation, double>(nameof(Slot1Progress));

    /// <summary>第三格的进度。</summary>
    public static readonly StyledProperty<double> Slot2ProgressProperty =
        AvaloniaProperty.Register<SlotMachineAnimation, double>(nameof(Slot2Progress));

    /// <summary>第四格的进度。</summary>
    public static readonly StyledProperty<double> Slot3ProgressProperty =
        AvaloniaProperty.Register<SlotMachineAnimation, double>(nameof(Slot3Progress));

    static SlotMachineAnimation()
    {
        // 四个进度属性里任何一个变了都要重画。
        AffectsRender<SlotMachineAnimation>(Slot0ProgressProperty, Slot1ProgressProperty,
            Slot2ProgressProperty, Slot3ProgressProperty);
    }

    /// <summary>每一格旋转时依次出现的字，最后一个必定是中选者的那个字；空序列表示这一格留空。</summary>
    private char[][] _spinSequences = [];

    /// <summary>
    /// 每一格的字轮播时间表：累加到这里就该换下一个字，数值是 0~1 的进度。
    /// </summary>
    /// <remarks>
    /// 长度就是这一格的序列长度。各格共用同一张表（每格转多久是一样的）。
    /// <para/>
    /// <b>用它而不是「对进度做缓动曲线」是有原因的。</b>原来写的是
    /// <c>floor(easeOutCubic(进度) × (字数 - 1))</c>，而缓动曲线只在进度恰好等于 1 时才取到 1——
    /// 也就是<b>结果那个字只在最后一刻出现、停留时间为零</b>，
    /// 倒数第二个字却从进度 0.55 一直霸占到结束（占整格 45% 的时间）。
    /// 看起来就是「卡在一个字上不动，然后啪地跳成答案」——
    /// 2026-09-15 主人报的「他的字会跳」就是这个。
    /// <para/>
    /// 换成时间表之后，每个字拿到一段实实在在的停留时间，而且是等比递增的
    /// （先快后慢），和滚动名字用的是同一套曲线，两个样式的节奏一致。
    /// </remarks>
    private double[] _spinThresholds = [];

    /// <summary>当前实际用了几格（2~4）。</summary>
    private int _slotCount;

    /// <summary>
    /// 每一格转多久。用它（而不是先化成秒数）去算关键帧时刻，
    /// 才能保证「最后一格的落点」和排片表给的 <c>MotionTotal</c> <b>一模一样</b>——
    /// 差一个 tick 都会让 cue 越过 1，Avalonia 的 Cue 构造函数会当场抛异常。
    /// </summary>
    private TimeSpan _slotStep = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 真正要转的格子数，从第 0 格数起。
    /// </summary>
    /// <remarks>
    /// 后面那些格子的候选字已经只剩唯一一种可能，再转也只是演戏——
    /// 它们不占时间（排片表的 <c>SpinSlotCount</c>）。
    /// </remarks>
    private int _spinSlotCount;

    /// <summary>
    /// 唯一候选那几格「一起亮出来」用多久。
    /// </summary>
    /// <remarks>
    /// 它们不等前面转完就亮会剧透：比如名单「张三、李四、王五」抽中张三，
    /// 第二格只剩「三」，一开场就显示的话，第一格还在转答案就漏了。
    /// 所以它们在<b>真正在转的格全部停稳的那一刻</b>才出现，这里留一小段淡入，
    /// 免得「啪」地跳出来。
    /// </remarks>
    private static readonly TimeSpan RevealRamp = TimeSpan.FromMilliseconds(140);

    /// <summary>
    /// 动作结束时刻：最后一格停住的那一瞬间，同时也是这段动画的 <c>Duration</c>。
    /// </summary>
    /// <remarks>
    /// 从自己的排片数据算（每格 × 要转的格数），不依赖「总时长里含不含定格」——
    /// 直接构造的排片表（测试，或将来别处复用）不一定带定格那一段。
    /// <para/>
    /// 定格不算在时长里：基类在播完之后原地等一会儿，那时所有格子都停在终值上。
    /// 唯一候选的那几格也在这一刻亮出来。
    /// </remarks>
    private TimeSpan _motionEnd;

    /// <summary>一个格子的宽度。</summary>
    private double _cellWidth;

    /// <summary>一个格子的高度。</summary>
    private double _cellHeight;

    /// <summary>格子之间的间距。</summary>
    private double _gap;

    /// <summary>板面左右两侧的留白。</summary>
    private double _padding;

    /// <summary>算好的舞台尺寸。</summary>
    private Size _stageSize;

    public SlotMachineAnimation(double revealFontSize, Color accent)
        : base(revealFontSize, accent)
    {
    }

    /// <summary>第一格的进度。</summary>
    public double Slot0Progress
    {
        get => GetValue(Slot0ProgressProperty);
        set => SetValue(Slot0ProgressProperty, value);
    }

    /// <summary>第二格的进度。</summary>
    public double Slot1Progress
    {
        get => GetValue(Slot1ProgressProperty);
        set => SetValue(Slot1ProgressProperty, value);
    }

    /// <summary>第三格的进度。</summary>
    public double Slot2Progress
    {
        get => GetValue(Slot2ProgressProperty);
        set => SetValue(Slot2ProgressProperty, value);
    }

    /// <summary>第四格的进度。</summary>
    public double Slot3Progress
    {
        get => GetValue(Slot3ProgressProperty);
        set => SetValue(Slot3ProgressProperty, value);
    }

    /// <summary>板凳面：几格宽、一格高。</summary>
    public override Size StageSize => _stageSize;

    /// <inheritdoc/>
    protected override void LoadPlan(RevealAnimationPlan plan)
    {
        // 喵~防御：装错了计划类型时什么都不画，也好过抛异常把结果卡住。
        if (plan is not SlotPlan slot)
        {
            _slotCount = 0;
            _motionEnd = TimeSpan.Zero;
            _stageSize = default;
            return;
        }

        // 格数夹在 2~4：排片表本来就该给出这个范围，这里再兜一道。
        _slotCount = Math.Clamp(slot.SlotCount, SlotMachine.MinSlots, SlotMachine.MaxSlots);

        // 喵~防御：单格时长不是正数时动画会一闪而过什么也看不见，兜底成 1 秒。
        // 用 TimeSpan 版的步长算关键帧时刻，和排片表的乘法是同一个，结果必定对齐。
        _slotStep = slot.Step > TimeSpan.Zero ? slot.Step : TimeSpan.FromSeconds(1);

        // 要转几格：后面那些候选字只剩唯一的格子不占时间，等前几格停稳了才亮出来。
        _spinSlotCount = Math.Clamp(slot.SpinSlotCount, 0, _slotCount);

        // 动作结束时刻 = 每格时长 × 要转的格数。它同时也是这段动画的 Duration。
        // 定格不在这里：基类会在播完之后原地等一会儿，那时所有格子都停在终值上。
        _motionEnd = _slotStep * _spinSlotCount;

        _spinSequences = new char[_slotCount][];

        // 这一格的字轮播时间表：等比递增的停留时长，先快后慢。
        // 直接复用滚动名字那套（ScrollTicks），两个样式的「抽」感一致，而且那套已经有单测钉着。
        var intervals = ScrollTicks.BuildIntervals(_slotStep.TotalSeconds);
        _spinThresholds = BuildThresholds(intervals);

        for (var i = 0; i < _slotCount; i++)
        {
            // 这一格的候选字（已排序去重，字符串里的每个字符就是一个候选）。
            var candidates = i < slot.Candidates.Count ? slot.Candidates[i] : null;
            // 中选者在这一格的字；null 表示这一格留空。
            var winnerChar = i < slot.WinnerChars.Count ? slot.WinnerChars[i] : null;
            // 这一格旋转时依次出现的字。
            // 要转的格子按时间表轮播；不转的格子（候选只剩唯一）只给一个字——
            // 给整条序列的话，最后那 140 毫秒的淡入里会飞快闪一串字，看着像花了屏。
            var entries = i < _spinSlotCount ? _spinThresholds.Length : 1;
            _spinSequences[i] = BuildSpinSequence(candidates, winnerChar, entries);
        }

        // 格子尺寸跟着中央大字的字号档位走。
        _cellHeight = RevealFontSize * 1.15;
        _cellWidth = RevealFontSize * 1.05;
        _gap = RevealFontSize * 0.14;
        _padding = RevealFontSize * 0.2;

        // 宽度 = 左右留白 + 所有格子 + 格子之间的缝；高度 = 一格再加一点上下留白放描边。
        _stageSize = new Size(
            _padding * 2 + _slotCount * _cellWidth + (_slotCount - 1) * _gap,
            _cellHeight + RevealFontSize * 0.3);
    }


    /// <summary>
    /// 按排片表造出这段动画。抽出来是为了让「最后一格停在哪一刻」能被单测验到。
    /// </summary>
    /// <remarks>
    /// <b>最后一格的落点就是动画的结尾，也就是排片表说的动作结束时刻。</b>
    /// 所有关键帧都落在这一刻或之前，于是 Avalonia 在最后一个关键帧之后保持终值不动
    /// （<c>FillMode.Forward</c>）——定格那一段由基类在播完之后原地等，画面就这么静止着，
    /// 让主人看清拼出来的名字。
    /// <para/>
    /// <b>候选字只剩唯一的那几格不转</b>：它们只有一个值可以取，转起来也是同一个字一直闪，
    /// 等于白等。这些格子在时刻 0 就被置成终值——板面一上来就是对的。
    /// 从第一格起全是唯一时，整段动作时长为 0，看见的是「板面直接出现、停一下、出结果」。
    /// <para/>
    /// 要是哪次改动把这些关键帧推到了动作时长之外，Avalonia 算 cue 时会当场抛异常，
    /// 整段动画静默消失，所以有测试盯着它。
    /// </remarks>
    internal override IReadOnlyList<Animation> BuildAnimations()
    {
        // 直线推每一格的进度；「先快后慢」的效果放在 Render 里做，
        // 这样时间轴本身保持简单——每格占的时间窗一眼就能看出来。
        var animation = new Animation
        {
            // 动画时长就是动作时长：最后一格在它末尾停住，缓动曲线铺满全程。
            // 定格由基类在播完之后等，不占这里的时长。
            Duration = _motionEnd,
            FillMode = FillMode.Forward,
            Easing = new LinearEasing()
        };

        for (var i = 0; i < _slotCount; i++)
        {
            // 这一格对应的进度属性。
            var property = SlotProgressProperty(i);

            // 候选字只剩唯一（或者中选者比格数短、这格留空）：不转。
            if (i >= _spinSlotCount)
            {
                AddRevealKeyFrames(animation, property);
                continue;
            }

            // 还没轮到这一格时钉在 0 上——板面上应该是个没字的空格子。
            animation.Children.Add(new KeyFrame
            {
                KeyTime = _slotStep * i,
                Setters = { new Setter(property, 0.0) }
            });
            // 轮到这一格：在它自己的时间窗里从 0 走到 1。
            // 最后一格的落点因此正好是「每格 × 要转的格数」，也就是排片表的 MotionTotal。
            animation.Children.Add(new KeyFrame
            {
                KeyTime = _slotStep * (i + 1),
                Setters = { new Setter(property, 1.0) }
            });
        }

        return [animation];
    }

    /// <summary>
    /// 给「候选只剩唯一」的格子加关键帧：它们<b>不在开头</b>就亮，而是在别格停稳的那一刻一起出现。
    /// </summary>
    /// <param name="animation">要往里加关键帧的动画。</param>
    /// <param name="property">这一格的进度属性。</param>
    /// <remarks>
    /// 之所以不放在开头：名单「张三、李四、王五」抽中张三时，第二格只剩「三」，
    /// 一开场就显示的话第一格还在转、答案就已经漏了。放在最后则是「中选者是谁最后一刻才齐」，
    /// 保住了「抽」的感觉。
    /// <para/>
    /// 起止时刻都取自动作结束时刻（<c>MotionTotal</c>），所以「关键帧的最大时刻 == 动作结束时刻」
    /// 这条不变量依然成立。
    /// </remarks>
    private void AddRevealKeyFrames(Animation animation, AvaloniaProperty<double> property)
    {
        // 动作结束时刻就是从自己这份排片数据算出来的「每格 × 要转的格数」。
        // 不写成「总时长 - 定格时长」是有原因的：定格是 RevealAnimationPlanner 统一加上去的，
        // 直接构造的排片表（测试，或将来别处复用）没有那一段，减出来就对不上，
        // 这几格会亮在「前面还在转」的时刻上。
        var motionEnd = _motionEnd;

        // 喵~防御：整段动作时长为 0（一格都不用转）时没有「停稳那一刻」，
        // 板面本来就该直接出现，于是只给一个终值关键帧。
        if (motionEnd <= TimeSpan.Zero)
        {
            animation.Children.Add(new KeyFrame
            {
                KeyTime = TimeSpan.Zero,
                Setters = { new Setter(property, 1.0) }
            });
            return;
        }

        // 淡入的起点。喵~防御：动作比淡入还短时从 0 开始，免得关键帧时刻变成负数。
        var rampStart = motionEnd - RevealRamp;
        if (rampStart < TimeSpan.Zero)
        {
            rampStart = TimeSpan.Zero;
        }

        // 之前一直空着。
        animation.Children.Add(new KeyFrame
        {
            KeyTime = rampStart,
            Setters = { new Setter(property, 0.0) }
        });
        // 到这一刻和前面停稳的格子一起亮出来。
        animation.Children.Add(new KeyFrame
        {
            KeyTime = motionEnd,
            Setters = { new Setter(property, 1.0) }
        });
    }

    /// <inheritdoc/>
    public override void SnapToEnd()
    {
        // 四格全部置 1：留空的格子没有旋转序列，画的时候照样是空框。
        Slot0Progress = 1;
        Slot1Progress = 1;
        Slot2Progress = 1;
        Slot3Progress = 1;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 喵~防御：尺寸还没量出来（0×0）或者没有格子时画不了，直接返回。
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _slotCount <= 0)
        {
            return;
        }

        // 板面纵向居中。
        var top = (Bounds.Height - _cellHeight) / 2;

        for (var i = 0; i < _slotCount; i++)
        {
            // 这一格在屏幕上的位置。
            var rect = new Rect(_padding + i * (_cellWidth + _gap), top, _cellWidth, _cellHeight);

            // 喵~防御：进度还没被动画写过时可能是 NaN，按「还没开始转」处理。
            var raw = SlotProgress(i);
            var progress = double.IsFinite(raw) ? Math.Clamp(raw, 0, 1) : 0;
            // 走到 1 就说明这一格定下来了。
            var settled = progress >= 1;

            // 这一格旋转时依次出现的字；长度 0 表示这一格留空。
            var sequence = _spinSequences[i];

            // 这一格当前该显示哪个字。留空格和还没开始的格子都是 null（什么都不画）。
            var shown = ShownCharAt(i, progress);

            // 格子底色：没定下来的暗一些，定下来的亮一些。
            var background = new SolidColorBrush(settled
                ? Color.FromRgb(0x2A, 0x2A, 0x34)
                : Color.FromRgb(0x1E, 0x1E, 0x26));
            // 定下来的格子用强调色描边，其余是很淡的白边。
            var border = settled
                ? new SolidColorBrush(Accent)
                : new SolidColorBrush(Colors.White, 0.16);
            // 定格的字用强调色，滚动中的字是白的。
            IBrush textBrush = settled ? new SolidColorBrush(Accent) : Brushes.White;

            DrawSlot(context, rect, background, border, settled ? 2 : 1.2, _cellHeight * 0.16);

            // 有字才画字：留空的格子就是一个空框。
            if (shown is not null)
            {
                // 把单个字放大到格子中间，左右留一点边。
                DrawFittedText(context, shown.Value.ToString(),
                    rect.Deflate(new Thickness(_cellWidth * 0.1, _cellHeight * 0.1)),
                    _cellHeight * 0.62, textBrush);
            }
        }
    }

    /// <summary>按格号取对应的进度属性。</summary>
    private static StyledProperty<double> SlotProgressProperty(int index) => index switch
    {
        0 => Slot0ProgressProperty,
        1 => Slot1ProgressProperty,
        2 => Slot2ProgressProperty,
        // 格数最多 4 格，越界的只会是 3。
        _ => Slot3ProgressProperty
    };

    /// <summary>取第 <paramref name="index"/> 格的当前进度。</summary>
    private double SlotProgress(int index) => GetValue(SlotProgressProperty(index));

    /// <summary>
    /// 拼出某一格旋转时依次出现的字。
    /// </summary>
    /// <param name="candidates">这一格的候选字（已排序去重）。</param>
    /// <param name="winnerChar">中选者在这一格的字；<c>null</c> 表示这一格留空。</param>
    /// <returns>依次出现的字，最后一个是 <paramref name="winnerChar"/>；留空的格子返回空数组。</returns>
    /// <summary>
    /// 某一格在给定进度下显示的字。
    /// </summary>
    /// <param name="slot">格号，从 0 起。</param>
    /// <param name="progress">这一格的进度，0~1。</param>
    /// <returns>要画的那个字；<c>null</c> 表示这一格什么都不画（留空，或还没轮到它）。</returns>
    /// <remarks>
    /// 渲染那条路也走这里，所以「什么时候显示哪个字」可以被单测直接验到——
    /// 光是测时间表和序列还不够：把渲染改回「对进度做缓动」的话，
    /// 时间表那几条测试照样绿，问题却已经回来了。
    /// </remarks>
    internal char? ShownCharAt(int slot, double progress)
    {
        // 喵~防御：格号越界（排片表被改过）时不画。
        if (slot < 0 || slot >= _spinSequences.Length)
        {
            return null;
        }

        var sequence = _spinSequences[slot];
        // 留空的格子没有字；进度还是 0 说明还没轮到这一格，板面上是个空框。
        if (sequence.Length == 0 || !(progress > 0))
        {
            return null;
        }

        // 按时间表查出该显示第几个字。
        return sequence[EntryIndexAt(_spinThresholds, progress, sequence.Length)];
    }

    /// <summary>
    /// 把「每一段停留多久」换算成「累加到多少进度就换下一个字」。
    /// </summary>
    /// <param name="intervals">每一段停留多久，单位：秒。各项之和就是这一格的总时长。</param>
    /// <returns>阈值数组，长度和 <paramref name="intervals"/> 一致，最后一项一定是 1。</returns>
    /// <remarks>换成的是一张递增的表，渲染时只要比一个数，不用每帧重算停留时长。</remarks>
    internal static double[] BuildThresholds(IReadOnlyList<double> intervals)
    {
        // 喵~防御：没有分段时给一张空表，调用方会当成「没有字可显示」。
        if (intervals is null || intervals.Count == 0)
        {
            return [];
        }

        // 先把总时长算出来，后面每一项都要除以它。
        var total = 0.0;
        foreach (var seconds in intervals)
        {
            // 喵~防御：坏值（负数、NaN）当 0 处理，否则总和会变成 NaN，整张表都废掉。
            total += double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
        }

        var thresholds = new double[intervals.Count];

        // 喵~防御：总时长为 0（时长配置被改坏）时平均分，最后一项仍然是 1。
        if (total <= 0)
        {
            for (var i = 0; i < thresholds.Length; i++)
            {
                thresholds[i] = (i + 1) / (double)thresholds.Length;
            }

            return thresholds;
        }

        var accumulated = 0.0;
        for (var i = 0; i < thresholds.Length; i++)
        {
            var seconds = double.IsFinite(intervals[i]) && intervals[i] > 0 ? intervals[i] : 0;
            accumulated += seconds;
            thresholds[i] = accumulated / total;
        }

        // 最后一项钉成 1：把浮点累加的小尾巴抹平，
        // 免得末尾差一点点，最后一个字永远显示不出来。
        thresholds[^1] = 1.0;
        return thresholds;
    }

    /// <summary>
    /// 查出进度落在时间表的第几段上，也就是「该显示序列里的第几个字」。
    /// </summary>
    /// <param name="thresholds">阈值数组，递增。</param>
    /// <param name="progress">当前进度，0~1。</param>
    /// <param name="sequenceLength">序列长度，用来夹住返回值。</param>
    /// <remarks>
    /// 主人注意：这里是一次线性扫描，每帧每格一次。表长跟着单格时长走
    /// （1.5 秒一格约 20 项，10 秒一格也不到 240 项），四格合起来每帧最多几百次比较，
    /// 相对 Skia 那边的绘制可以忽略。真要更省可以改成二分查找。
    /// </remarks>
    internal static int EntryIndexAt(IReadOnlyList<double> thresholds, double progress, int sequenceLength)
    {
        // 喵~防御：序列是空的，或者进度不是有限数（动画还没写过它）时回第 0 项。
        if (thresholds is null || thresholds.Count == 0 || !double.IsFinite(progress))
        {
            return 0;
        }

        for (var i = 0; i < thresholds.Count; i++)
        {
            if (progress <= thresholds[i])
            {
                // 第一段「进度还没越过」的就是它。
                return Math.Clamp(i, 0, sequenceLength - 1);
            }
        }

        // 进度比最后一段还大（浮点误差）→ 落在最后一个字上。
        return Math.Max(0, sequenceLength - 1);
    }

    /// <summary>
    /// 造出这一格旋转时依次出现的字。
    /// </summary>
    /// <param name="candidates">这一格的候选字，每个字符一个候选，已排序。</param>
    /// <param name="winnerChar">中选者在这一格的字；<c>null</c> 表示这一格留空。</param>
    /// <param name="entryCount">要造几个字，也就是要换几次。</param>
    /// <returns>依次出现的字；留空格返回空数组。</returns>
    /// <remarks>
    /// <b>前 <c>entryCount - 1</c> 个按顺序轮着取候选字——包括结果自己。</b>
    /// 只有两个候选时（比如「张」「李」），把结果排除在外会让这一格一直闪同一个字，
    /// 看着像卡住了，最后才突然跳成结果。结果字在旋转途中闪过是正常的：
    /// 它本来就是候选之一，闪过几次不代表「已经抽出来了」。
    /// <para/>
    /// 唯一要躲开的是「倒数第二个正好是结果」——那看起来像早就停了。
    /// </remarks>
    internal static char[] BuildSpinSequence(string? candidates, char? winnerChar, int entryCount)
    {
        // 喵~防御：留空的格子（中选者比格数短）没有字可转，给一个空序列。
        if (winnerChar is null || string.IsNullOrEmpty(candidates))
        {
            return [];
        }

        // 喵~防御：要 0 个或负数个字时给空序列，调用方会当成「没有字可显示」。
        if (entryCount <= 0)
        {
            return [];
        }

        // 不转的格子（候选只剩唯一）只给一个字。
        // 给整条序列的话，最后那 140 毫秒的淡入里会飞快闪一串字，看着像花了屏。
        if (entryCount == 1)
        {
            return [winnerChar.Value];
        }

        var sequence = new char[entryCount];

        // 前面按候选顺序轮着取，取完一轮从头再来。
        for (var i = 0; i < entryCount - 1; i++)
        {
            sequence[i] = candidates[i % candidates.Length];
        }

        // 倒数第二个如果正好是结果，换成别的候选。
        // 喵~防御：候选只有结果自己时没有别人可换，那就保持原样——
        // 这种格子本来就不该转（排片表会把它排除在 SpinSlotCount 之外）。
        if (sequence[entryCount - 2] == winnerChar.Value)
        {
            var alternative = FindOtherCandidate(candidates, winnerChar.Value);
            if (alternative is { } other)
            {
                sequence[entryCount - 2] = other;
            }
        }

        // 最后一个字必定是中选者的那个字。
        sequence[entryCount - 1] = winnerChar.Value;
        return sequence;
    }

    /// <summary>从候选里挑一个不是结果的字，用来避免「倒数第二下就出现结果」。</summary>
    /// <returns>挑不到（候选只有结果自己）时返回 <c>null</c>。</returns>
    private static char? FindOtherCandidate(string candidates, char winnerChar)
    {
        foreach (var candidate in candidates)
        {
            if (candidate != winnerChar)
            {
                return candidate;
            }
        }

        return null;
    }
}
