using System;
using System.Linq;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 滚动名字样式的换帧时刻表测试。
/// </summary>
/// <remarks>
/// 这段的核心不变量有三条：各帧时长加起来<b>正好</b>等于配置的总时长、
/// 时长逐帧变长（也就是先快后慢）、以及增长是<b>等比</b>的而非等差（也就是非线性）。
/// 塌了任何一条，用户看到的就是「动画比设置里写的长了一截」
/// 或者「说好的减速变成了匀速」。
/// </remarks>
public sealed class ScrollTicksTests
{
    #region 换帧间隔

    [Fact]
    public void BuildIntervals_SumsToExactlyTheTotalDuration()
    {
        // 这是最关键的不变量：配置写了几秒，实际就得播几秒。
        // 逐帧乘减速系数的老做法会累积误差，这里靠反解首项把它消掉。
        foreach (var total in new[] { 0.5, 1.0, 2.5, 4.0, 7.3, 12.0, 20.0 })
        {
            var intervals = ScrollTicks.BuildIntervals(total);
            Assert.Equal(total, intervals.Sum(), 6);
        }
    }

    [Fact]
    public void BuildIntervals_IsStrictlyIncreasing()
    {
        // 先快后慢：后一帧一定比前一帧停留得久。
        var intervals = ScrollTicks.BuildIntervals(4.0);

        for (var i = 1; i < intervals.Length; i++)
        {
            Assert.True(intervals[i] > intervals[i - 1],
                $"第 {i} 帧（{intervals[i]:F4}s）不比第 {i - 1} 帧（{intervals[i - 1]:F4}s）长，就不是减速了");
        }
    }

    [Fact]
    public void BuildIntervals_GrowsGeometricallyNotLinearly()
    {
        // 这条把「非线性」钉死。
        // 等差数列的相邻间隔之差是常数；等比数列的相邻间隔之<b>比</b>才是常数。
        // 只用「严格递增」那一条是区分不出两者的——线性减速也严格递增，
        // 但那正是被换掉的老做法。
        var intervals = ScrollTicks.BuildIntervals(8.0);

        for (var i = 2; i < intervals.Length; i++)
        {
            // 相邻两段增长率。
            var previousRatio = intervals[i - 1] / intervals[i - 2];
            var currentRatio = intervals[i] / intervals[i - 1];
            // 等比数列下这两个比值是同一个常数（浮点误差范围内）。
            Assert.True(Math.Abs(currentRatio - previousRatio) < 0.01,
                $"第 {i} 段的增长倍数 {currentRatio:F4} 和第 {i - 1} 段的 {previousRatio:F4} 差太多，不是等比增长");
            // 顺便确认它确实是「增长」而不是衰减。
            Assert.True(currentRatio > 1.05, $"第 {i} 段的增长倍数只有 {currentRatio:F4}，减速太弱了");
        }
    }

    [Fact]
    public void BuildIntervals_LinearIntervals_WouldFailTheGeometricCheck()
    {
        // 反证：手搓一个等差数列，上面那条断言必须能识别出它不是等比。
        // 没有这一条，「非线性」的断言可能只是碰巧成立。
        var linear = new double[12];
        for (var i = 0; i < linear.Length; i++)
        {
            // 从 0.02 线性涨到 0.30。
            linear[i] = 0.02 + (0.30 - 0.02) * i / (linear.Length - 1);
        }

        // 等差数列相邻两段的增长倍数一直在变小（分母在变大），不是常数。
        var firstRatio = linear[1] / linear[0];
        var lastRatio = linear[^1] / linear[^2];
        Assert.True(Math.Abs(firstRatio - lastRatio) > 0.01,
            "等差数列的增长倍数居然被当成了常数，说明等比那条断言没有鉴别力");
    }

    [Fact]
    public void BuildIntervals_FirstIsMuchShorterThanLast()
    {
        // 首尾差距要拉开，「飞快滚过 → 慢慢停下」的感觉才出得来。
        var intervals = ScrollTicks.BuildIntervals(4.0);

        Assert.True(intervals[^1] > intervals[0] * 4,
            $"首帧 {intervals[0]:F4}s、末帧 {intervals[^1]:F4}s，差距太小");
    }

    [Fact]
    public void BuildIntervals_FirstTickIsShortEnoughToBlur()
    {
        // 开头必须快到看不清，否则就不像「飞快滚过」而像逐字念。
        var intervals = ScrollTicks.BuildIntervals(4.0);

        Assert.True(intervals[0] <= 0.04,
            $"首帧停了 {intervals[0]:F4}s，太慢，开头不会糊成一片");
    }

    [Fact]
    public void BuildIntervals_LastTickIsSlowEnoughToRead()
    {
        // 收尾必须慢到看得清最后那个名字。
        var intervals = ScrollTicks.BuildIntervals(4.0);

        Assert.True(intervals[^1] >= 0.15,
            $"末帧只停了 {intervals[^1]:F4}s，还没看清就没了");
    }

    [Fact]
    public void BuildIntervals_HasAtLeastTwoTicks()
    {
        // 至少两帧才谈得上「滚动」。
        Assert.True(ScrollTicks.BuildIntervals(4.0).Length >= 2);
    }

    [Fact]
    public void BuildIntervals_WithTinyDuration_StillSumsToTotal()
    {
        // 时长特别短时仍然要满足「总和精确等于配置值」这条底线。
        var intervals = ScrollTicks.BuildIntervals(0.05);

        Assert.Equal(0.05, intervals.Sum(), 6);
    }

    [Fact]
    public void BuildIntervals_WithHugeDuration_IsCapped()
    {
        // 喵~防御：总时长被改成极大值时不能算出几千帧，否则每一帧都在重绘上白烧 CPU。
        var intervals = ScrollTicks.BuildIntervals(600.0);

        Assert.True(intervals.Length <= ScrollTicks.MaxTicks,
            $"帧数 {intervals.Length} 超过了上限 {ScrollTicks.MaxTicks}");
    }

    [Fact]
    public void BuildIntervals_WithZeroDuration_ReturnsSingleTick()
    {
        // 时长为 0：退化成只有一帧，效果等同于瞬间定格。
        Assert.Single(ScrollTicks.BuildIntervals(0));
    }

    [Fact]
    public void BuildIntervals_WithNegativeDuration_ReturnsSingleTick()
    {
        // 负数时长同理，不能算出负数的帧数来。
        Assert.Single(ScrollTicks.BuildIntervals(-5));
    }

    [Fact]
    public void BuildIntervals_WithNaN_ReturnsSingleTick()
    {
        // 喵~防御：配置被改成 NaN 时不能让它传播下去。
        Assert.Single(ScrollTicks.BuildIntervals(double.NaN));
    }

    [Fact]
    public void BuildIntervals_WithInfinity_ReturnsSingleTick()
    {
        // 无穷大同理。
        Assert.Single(ScrollTicks.BuildIntervals(double.PositiveInfinity));
    }

    #endregion

    #region 排片表

    [Fact]
    public void BuildPlan_LastFrameIsAlwaysTheWinner()
    {
        // 动画必须定格在中选者身上，这是「结果早就抽好了、动画只是演」这条设计的落点。
        var plan = ScrollTicks.BuildPlan(["张三", "李四", "王五"], "李四", 4.0);

        Assert.Equal("李四", plan.Frames[^1]);
        Assert.Equal("李四", plan.Winner);
    }

    [Fact]
    public void BuildPlan_FrameCountMatchesIntervalCount()
    {
        // 每一帧都得有对应的停留时长，不然播放时会错位。
        var plan = ScrollTicks.BuildPlan(["张三", "李四"], "张三", 4.0);

        Assert.Equal(plan.Intervals.Count, plan.Frames.Count);
    }

    [Fact]
    public void BuildPlan_TotalMatchesTheSumOfIntervals()
    {
        // 计划里写的总时长必须和实际要播的一致。
        var plan = ScrollTicks.BuildPlan(["张三", "李四"], "张三", 4.0);

        Assert.Equal(plan.Intervals.Sum(), plan.Total.TotalSeconds, 6);
    }

    /// <summary>
    /// 帧序就是名单循环展开的一段：相邻两帧在名单里也相邻，滚起来才像「滚动」。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「参考 csgo 的滚动机制」建的。</b>
    /// 早先的写法是每一帧从「其他人」里随机挑，名字在乱跳；换帧的间隔明明在等比变长，
    /// 画面却看不出节奏，减速感全被跳没了——主人说的「他最后变慢的过程似乎消失了」。
    /// 换成名单循环之后，名字一个挨一个滚过去，跟 CSGO 拼轨道是同一个思路。
    /// </remarks>
    [Fact]
    public void BuildPlan_FramesFollowTheRosterOrderUpToTheWinner()
    {
        // 三人的名单，末帧是中选者「李四」。
        string[] roster = ["张三", "李四", "王五"];
        var plan = ScrollTicks.BuildPlan(roster, "李四", 4.0);

        Assert.Equal("李四", plan.Frames[^1]);

        // 顺着往前推：每一帧都该紧挨着下一帧在名单里的前一个，走完一轮就绕到名单末尾。
        for (var i = 0; i < plan.Frames.Count - 1; i++)
        {
            // 下一帧在名单里的位置。
            var nextIndex = Array.IndexOf(roster, plan.Frames[i + 1]);
            // 这一帧在名单里的位置。
            var currentIndex = Array.IndexOf(roster, plan.Frames[i]);
            // 「下一帧的前一个」，绕圈时从开头回到末位。
            var expected = (nextIndex - 1 + roster.Length) % roster.Length;

            Assert.Equal(expected, currentIndex);
        }
    }

    /// <summary>名单比帧数短时按名单循环重复，滚的始终是名单里的人。</summary>
    [Fact]
    public void BuildPlan_WithShortRoster_RepeatsTheRosterInOrder()
    {
        var plan = ScrollTicks.BuildPlan(["张三", "李四"], "张三", 4.0);

        // 帧数远多于人数，但每一帧都得是名单里的人。
        Assert.True(plan.Frames.Count > 2);
        Assert.All(plan.Frames, frame => Assert.True(frame is "张三" or "李四"));
        // 末帧仍然是中选者。
        Assert.Equal("张三", plan.Frames[^1]);
    }

    [Fact]
    public void BuildPlan_WithSingleName_EveryFrameShowsThatName()
    {
        // 名单里只有中选者一个人：没别人可滚，每帧都显示他。
        var plan = ScrollTicks.BuildPlan(["张三"], "张三", 4.0);

        Assert.All(plan.Frames, frame => Assert.Equal("张三", frame));
    }

    [Fact]
    public void BuildPlan_WithNullRoster_EveryFrameShowsWinner()
    {
        // 喵~防御：名单为 null 时按空名单处理，而不是崩掉。
        var plan = ScrollTicks.BuildPlan(null, "张三", 4.0);

        Assert.All(plan.Frames, frame => Assert.Equal("张三", frame));
    }

    [Fact]
    public void BuildPlan_WithWinnerNotInRoster_ShowsOnlyTheWinner()
    {
        // 喵~防御：中选者不在名单里（动画开播前名单被改过）时没有顺序可排，
        // 每一帧都给他——效果等同于「定格在他身上」，而不是崩掉或者显示别人。
        var plan = ScrollTicks.BuildPlan(["张三", "李四"], "王五", 4.0);

        Assert.All(plan.Frames, frame => Assert.Equal("王五", frame));
    }

    [Fact]
    public void BuildPlan_WithZeroDuration_ProducesSingleFrame()
    {
        // 时长为 0 时只有一帧，而且就是中选者——等同于直接出结果。
        var plan = ScrollTicks.BuildPlan(["张三", "李四"], "李四", 0);

        Assert.Single(plan.Frames);
        Assert.Equal("李四", plan.Frames[0]);
    }

    #endregion
}
