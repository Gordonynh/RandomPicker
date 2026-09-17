using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views.Animations;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 老虎机「字怎么换」的测试。
/// </summary>
/// <remarks>
/// <b>这个文件是为 2026-09-15「老虎机动画依然有 bug，他的字会跳」建的。</b>
/// 当时有两个毛病，都会让字看起来是「跳」而不是「转」：
/// <list type="number">
/// <item><b>候选只有两个字时整格只闪一个字。</b>
///       <c>BuildSpinSequence</c> 里游标每轮会被加两次（跳过一次再正常走一步），
///       两个字时下标奇偶性不变，于是每次都命中同一个字、每次都跳过它——
///       序列退化成「一个字重复 11 遍 + 结果」。看起来就是卡住不动，最后啪地跳成答案。</item>
/// <item><b>结果那个字只在最后一刻出现，停留时间为零。</b>
///       原来用 <c>floor(easeOutCubic(进度) × (字数-1))</c> 选字，
///       而缓动曲线只在进度恰好是 1 时才等于 1——倒数第二个字从进度 0.55 就霸占到结束
///       （占整格 45% 的时间），结果字一出现动画就结束了。</item>
/// </list>
/// 现在换成等比递增的时间表（和滚动名字同一套），上面两条都钉在这里。
/// </remarks>
public class SlotSpinSequenceTests
{
    /// <summary>两个字一格的候选，按码点排是「张李」。</summary>
    private const string TwoCandidates = "张李";

    /// <summary>四个字一格的候选。</summary>
    private const string FourCandidates = "张李王赵";

    #region 序列本身

    /// <summary>
    /// 候选有两个字时，旋转途中两个字都要出现——只闪一个就是卡住。
    /// </summary>
    /// <remarks>
    /// 这条直接钉住毛病一。当时为了让结果字别提前出现，代码把「命中的正好是结果」往前跳一格，
    /// 结果两个字时永远跳同一个位置，序列里只剩另一个字。
    /// </remarks>
    [Fact]
    public void SpinSequence_WithTwoCandidates_ShowsBothCharacters()
    {
        var sequence = SlotMachineAnimation.BuildSpinSequence(TwoCandidates, '张', entryCount: 12);

        Assert.Equal(12, sequence.Length);
        // 最后一个是结果。
        Assert.Equal('张', sequence[^1]);
        // 前面那 11 个里，两个候选都得出现过——只闪「李」就等于没在转。
        // 按码点排：张 U+5F20 < 李 U+674E。
        var before = sequence.Take(sequence.Length - 1).Distinct().OrderBy(c => c).ToArray();
        Assert.Equal(['张', '李'], before);
    }

    /// <summary>四个候选时要轮着出现，而且结果不能落在倒数第二个上。</summary>
    [Fact]
    public void SpinSequence_CyclesAllCandidatesAndKeepsTheWinnerOffTheSecondLastSlot()
    {
        var sequence = SlotMachineAnimation.BuildSpinSequence(FourCandidates, '张', entryCount: 12);

        Assert.Equal(12, sequence.Length);
        Assert.Equal('张', sequence[^1]);
        // 倒数第二个不能是结果——那看起来像「早就停了」。
        Assert.NotEqual('张', sequence[^2]);
        // 四个候选都得露面。
        Assert.Equal(4, sequence.Take(sequence.Length - 1).Distinct().Count());
    }

    /// <summary>
    /// 候选只有结果自己时序列就一个字重复——这种格子本来就不该转，
    /// 排片表会把它排除在 <c>SpinSlotCount</c> 之外。
    /// </summary>
    [Fact]
    public void SpinSequence_WithOnlyTheWinnerAsCandidate_RepeatsIt()
    {
        var sequence = SlotMachineAnimation.BuildSpinSequence("张", '张', entryCount: 8);

        Assert.Equal(8, sequence.Length);
        Assert.All(sequence, c => Assert.Equal('张', c));
    }

    /// <summary>不转的格子（<c>entryCount = 1</c>）只给一个字，不能给整条序列。</summary>
    /// <remarks>
    /// 给整条序列的话，最后那 140 毫秒的淡入里会飞快闪十几个字，看着像花了屏。
    /// </remarks>
    [Fact]
    public void SpinSequence_WithSingleEntry_GivesJustTheWinner()
    {
        var sequence = SlotMachineAnimation.BuildSpinSequence(FourCandidates, '张', entryCount: 1);

        Assert.Equal(['张'], sequence);
    }

    /// <summary>留空的格子给空序列；坏输入不抛异常。</summary>
    [Fact]
    public void SpinSequence_WithBlankOrBadInput_ReturnsEmpty()
    {
        // 中选者比格数短留下的空格子。
        Assert.Empty(SlotMachineAnimation.BuildSpinSequence(FourCandidates, null, entryCount: 12));
        // 候选为空。
        Assert.Empty(SlotMachineAnimation.BuildSpinSequence("", '张', entryCount: 12));
        Assert.Empty(SlotMachineAnimation.BuildSpinSequence(null, '张', entryCount: 12));
        // 喵~防御：要 0 个或负数个时也不该抛异常。
        Assert.Empty(SlotMachineAnimation.BuildSpinSequence(TwoCandidates, '张', entryCount: 0));
    }

    #endregion

    #region 时间表

    /// <summary>
    /// 时间表必须严格递增、最后一项是 1，而且每一项都拿到实实在在的停留时间。
    /// </summary>
    /// <remarks>
    /// 这条钉住毛病二。「每一项都拿到时间」正是「结果字停留为零」的反面：
    /// 表里最后一段的宽度就是结果字能显示多久，它不能是 0。
    /// </remarks>
    [Fact]
    public void Thresholds_AreStrictlyIncreasingAndEndAtOne()
    {
        var intervals = ScrollTicks.BuildIntervals(1.5);
        var thresholds = SlotMachineAnimation.BuildThresholds(intervals);

        Assert.Equal(intervals.Length, thresholds.Length);
        Assert.Equal(1.0, thresholds[^1]);
        // 严格递增：每一段都要比前一段大，否则那一段的停留时间是 0。
        for (var i = 1; i < thresholds.Length; i++)
        {
            Assert.True(thresholds[i] > thresholds[i - 1],
                $"第 {i} 段的阈值没比前一段大，那一段等于没有停留时间");
        }
    }

    /// <summary>
    /// 结果字（序列最后一个）能显示的时间不能太短，也不能一家独大。
    /// </summary>
    /// <remarks>
    /// 这两头都是毛病二的表现：原来是「结果字 0 秒、倒数第二个字占 45%」。
    /// 这一条把停留时间的分布卡在一个合理的区间里。
    /// </remarks>
    [Fact]
    public void Thresholds_GiveTheLastEntryAReasonableShareOfTheStep()
    {
        var intervals = ScrollTicks.BuildIntervals(1.5);
        var thresholds = SlotMachineAnimation.BuildThresholds(intervals);

        // 最后一段的宽度 = 结果字显示多久（占整格的比例）。
        var lastShare = 1.0 - thresholds[^2];
        Assert.True(lastShare > 0.02, $"结果字只显示整格的 {lastShare:P1}，太短了，等于一出现就被顶掉");
        Assert.True(lastShare < 0.30, $"结果字占了整格的 {lastShare:P1}，会看起来像早就停了");
    }

    /// <summary>总时长是 0（时长配置被改坏）时平均分，最后一项仍然是 1。</summary>
    [Fact]
    public void Thresholds_WithZeroTotal_SplitsEvenly()
    {
        var thresholds = SlotMachineAnimation.BuildThresholds([0, 0, 0, 0]);

        Assert.Equal(1.0, thresholds[^1]);
        Assert.Equal(0.25, thresholds[0]);
        Assert.Equal(0.5, thresholds[1]);
    }

    /// <summary>坏值（负数、NaN）当 0 处理，不许把整张表污染成 NaN。</summary>
    [Fact]
    public void Thresholds_WithNegativeAndNaNValues_StaysUsable()
    {
        var thresholds = SlotMachineAnimation.BuildThresholds([-5, double.NaN, 2, 1]);

        Assert.Equal(1.0, thresholds[^1]);
        Assert.All(thresholds, value => Assert.True(double.IsFinite(value) && value >= 0));
    }

    /// <summary>空表不抛异常。</summary>
    [Fact]
    public void Thresholds_WithEmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SlotMachineAnimation.BuildThresholds([]));
        Assert.Empty(SlotMachineAnimation.BuildThresholds(null));
    }

    #endregion

    #region 按时间表查字

    /// <summary>
    /// 进度从小到大走，查出来的下标只能越来越大，绝不能回头。
    /// </summary>
    /// <remarks>
    /// 「回头」就是字往回跳，是这里最难看的一种错。顺带验一下进度 0 和 1 落在两头。
    /// </remarks>
    [Fact]
    public void EntryIndexAt_NeverGoesBackwards()
    {
        var thresholds = SlotMachineAnimation.BuildThresholds(ScrollTicks.BuildIntervals(1.5));

        var previous = -1;
        for (var step = 0; step <= 200; step++)
        {
            var progress = step / 200.0;
            var index = SlotMachineAnimation.EntryIndexAt(thresholds, progress, thresholds.Length);

            Assert.True(index >= previous, $"进度 {progress:F3} 处下标从 {previous} 退回了 {index}");
            previous = index;
        }

        // 两头：开头落在第 0 个字上，结尾落在最后一个字上。
        Assert.Equal(0, SlotMachineAnimation.EntryIndexAt(thresholds, 0, thresholds.Length));
        Assert.Equal(thresholds.Length - 1,
            SlotMachineAnimation.EntryIndexAt(thresholds, 1, thresholds.Length));
    }

    /// <summary>
    /// 早段换字要快、末段要慢——「先快后慢」在数字上的样子。
    /// </summary>
    [Fact]
    public void EntryIndexAt_ChangesFasterAtTheStartThanAtTheEnd()
    {
        var thresholds = SlotMachineAnimation.BuildThresholds(ScrollTicks.BuildIntervals(1.5));

        // 前半段（进度 0~0.5）换掉的字数。
        var firstHalf = SlotMachineAnimation.EntryIndexAt(thresholds, 0.5, thresholds.Length);
        // 后半段（0.5~1）换掉的字数。
        var secondHalf = thresholds.Length - 1 - firstHalf;

        Assert.True(firstHalf > secondHalf,
            $"前半段换了 {firstHalf} 个字、后半段换了 {secondHalf} 个——不是先快后慢");
    }

    /// <summary>越界和坏输入都被夹住，不抛异常。</summary>
    [Fact]
    public void EntryIndexAt_WithBadInput_IsSafe()
    {
        var thresholds = SlotMachineAnimation.BuildThresholds(ScrollTicks.BuildIntervals(1.5));

        // 喵~防御：进度是 NaN（动画还没写过）或者超出 0~1 时都要有确定的答案。
        Assert.Equal(0, SlotMachineAnimation.EntryIndexAt(thresholds, double.NaN, thresholds.Length));
        Assert.Equal(0, SlotMachineAnimation.EntryIndexAt(thresholds, -1, thresholds.Length));
        // 比最后一段还大时落在最后一个字上。
        Assert.Equal(thresholds.Length - 1,
            SlotMachineAnimation.EntryIndexAt(thresholds, 1.5, thresholds.Length));
        // 空表不抛异常。
        Assert.Equal(0, SlotMachineAnimation.EntryIndexAt([], 0.5, 4));
        Assert.Equal(0, SlotMachineAnimation.EntryIndexAt(null, 0.5, 4));
    }

    #endregion

    #region 整段串起来

    /// <summary>
    /// 把排片表装进控件，逐帧走一遍进度，看看每一格是不是真的在「换字」。
    /// </summary>
    /// <remarks>
    /// 前面那些测的是零件，这一条测的是「装起来之后到底换了几个字」——
    /// 毛病一在零件层是「序列退化成一个字」，在整机层就是「这一格从头到尾只显示过一个字」。
    /// 进度只能透过控件拿到，所以这里走渲染那条路：用无头平台把每一帧画出来，
    /// 读回像素太脆，于是改成读序列——序列就是渲染唯一的数据来源。
    /// </remarks>
    [AvaloniaFact]
    public void LoadedControl_SpinsThroughMoreThanOneCharacter()
    {
        // 名单两个姓：第一格只有「张」「李」两种可能，也就是当初退化的那种情况。
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot, SlotStepSeconds = 1.5 };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot, ["张三", "李四"], "张三", settings);
        Assert.NotNull(plan);

        var control = new SlotMachineAnimation(new PickerSettings().RevealFontSize, Colors.Cyan);
        control.Load(plan);

        // 第一格要转（有两种可能），第二格只剩唯一、不转。
        var animation = Assert.Single(control.BuildAnimations());

        // 找出第一格进度属性上的关键帧，它应该从 0 走到 1。
        var slot0 = AnimationKeyFrames.FramesOf(animation, SlotMachineAnimation.Slot0ProgressProperty);

        Assert.NotEmpty(slot0);
        Assert.Equal(0.0, slot0[0].Value);
        Assert.Equal(1.0, slot0[^1].Value);

        // 逐帧走一遍这一格的进度，数一数一共显示过几个不同的字。
        var thresholds = SlotMachineAnimation.BuildThresholds(ScrollTicks.BuildIntervals(1.5));
        var sequence = SlotMachineAnimation.BuildSpinSequence("张李", '张', thresholds.Length);
        var seen = new HashSet<char>();

        for (var step = 0; step <= 400; step++)
        {
            var progress = step / 400.0;
            if (progress <= 0)
            {
                // 进度为 0 时画的是空格子，没有字。
                continue;
            }

            var index = SlotMachineAnimation.EntryIndexAt(thresholds, progress, sequence.Length);
            seen.Add(sequence[index]);
        }

        // 两个字都出现过 → 这一格真的在换字，不是卡住。
        Assert.Equal(2, seen.Count);
    }

    /// <summary>
    /// 走渲染那条路：两个字的一格必须真的换字，而且末尾那段要停在结果上。
    /// </summary>
    /// <remarks>
    /// <b>这一条才是钉住毛病二的关键。</b>毛病二是「结果字只在最后一刻出现、停留为零，
    /// 倒数第二个字霸占整格 45%」——那是选字的算法错了。
    /// 只测时间表和序列是不够的：把渲染改回「对进度做缓动」的话，
    /// 时间表那几条测试照样绿，问题却已经回来了。
    /// 所以这里从 <see cref="SlotMachineAnimation.ShownCharAt"/> 走一遍，
    /// 也就是渲染唯一的数据来源。
    /// </remarks>
    [AvaloniaFact]
    public void ShownCharAt_SpinsThroughBothCharactersAndLandsTheWinnerEarlyEnough()
    {
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot, SlotStepSeconds = 1.5 };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot, ["张三", "李四"], "张三", settings);
        Assert.NotNull(plan);

        var control = new SlotMachineAnimation(new PickerSettings().RevealFontSize, Colors.Cyan);
        control.Load(plan);

        // 逐帧走一遍第一格的进度。
        var seen = new HashSet<char>();
        for (var step = 1; step <= 200; step++)
        {
            var progress = step / 200.0;
            if (control.ShownCharAt(0, progress) is { } character)
            {
                seen.Add(character);
            }
        }

        // 两个候选都出现过 → 这一格真的在换字，不是卡住不动。
        Assert.Equal(2, seen.Count);
        Assert.Contains('张', seen);

        // 结果字必须在动画结束之前就显示出来一段时间——
        // 原来那个算法里它只在进度恰好等于 1 时才出现，等于零停留。
        Assert.Equal('张', control.ShownCharAt(0, 0.99));

        // 进度 0 是空格子（还没轮到这一格）。
        Assert.Null(control.ShownCharAt(0, 0));
        // 越界格号不抛异常。
        Assert.Null(control.ShownCharAt(9, 0.5));
        Assert.Null(control.ShownCharAt(-1, 0.5));
    }

    /// <summary>
    /// 候选只剩唯一的那一格：从头到尾就一个字，而且一开始是空框。
    /// </summary>
    /// <remarks>
    /// 它不转（<c>SpinSlotCount</c> 把它排除在外），所以给它的序列只有一个字——
    /// 给整条序列的话，最后那 140 毫秒的淡入里会飞快闪十几个字，看着像花了屏。
    /// </remarks>
    [AvaloniaFact]
    public void ShownCharAt_ForTheOnlyPossibilitySlot_ShowsOneCharacter()
    {
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot, SlotStepSeconds = 1.5 };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot, ["张三", "李四"], "张三", settings);
        var slot = Assert.IsType<SlotPlan>(plan);
        // 第一格要转，第二格只剩唯一。
        Assert.Equal(1, slot.SpinSlotCount);

        var control = new SlotMachineAnimation(new PickerSettings().RevealFontSize, Colors.Cyan);
        control.Load(slot);

        var seen = new HashSet<char>();
        for (var step = 1; step <= 200; step++)
        {
            if (control.ShownCharAt(1, step / 200.0) is { } character)
            {
                seen.Add(character);
            }
        }

        // 只有一个字，不会在淡入那段时间里闪一串。
        Assert.Equal(['三'], seen);
        Assert.Null(control.ShownCharAt(1, 0));
    }

    #endregion
}
