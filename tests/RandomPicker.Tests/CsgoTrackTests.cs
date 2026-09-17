using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// CSGO 开箱样式的轨道几何测试。
/// </summary>
/// <remarks>
/// 这段的核心是一条恒等式：<see cref="CsgoTrack.OffsetFor"/> 算出的平移量，
/// 拿 <see cref="CsgoTrack.IndexAtOffset"/> 反查回来必须还是同一个下标。
/// 只要这条成立，「滚到终点时指针下面就是中选者」就是被证明过的，而不是碰运气的。
/// </remarks>
public sealed class CsgoTrackTests
{
    /// <summary>可视区宽度，单位：逻辑像素。</summary>
    private const double ViewportWidth = 400;

    /// <summary>可视区高度，单位：逻辑像素。</summary>
    private const double ViewportHeight = 80;

    /// <summary>方块宽度，单位：逻辑像素。</summary>
    private const double SlotWidth = 120;

    /// <summary>方块高度，单位：逻辑像素。</summary>
    private const double SlotHeight = 70;

    /// <summary>方块间距，单位：逻辑像素。</summary>
    private const double Gap = 10;

    /// <summary>按测试用的固定尺寸造一份排片表。</summary>
    private static CsgoPlan? Build(IReadOnlyList<string>? names, string? winner) =>
        CsgoTrack.Build(names, winner, ViewportWidth, ViewportHeight, SlotWidth, SlotHeight, Gap,
            TimeSpan.FromSeconds(4));

    #region 核心恒等式

    [Fact]
    public void OffsetFor_ThenIndexAtOffset_RoundTrips()
    {
        // 滚到「让第 k 块居中」的位置，反查回来必须还是第 k 块。
        for (var index = 0; index < 60; index++)
        {
            var offset = CsgoTrack.OffsetFor(index, SlotWidth, Gap, ViewportWidth);
            Assert.Equal(index, CsgoTrack.IndexAtOffset(offset, SlotWidth, Gap, ViewportWidth));
        }
    }

    [Fact]
    public void OffsetFor_IsMonotonic()
    {
        // 下标越大，需要滚的距离越远——不然会滚出「倒着走」的怪現象。
        var previous = double.NegativeInfinity;
        for (var index = 0; index < 20; index++)
        {
            var offset = CsgoTrack.OffsetFor(index, SlotWidth, Gap, ViewportWidth);
            Assert.True(offset > previous, $"第 {index} 块的平移量没有比上一块更远");
            previous = offset;
        }
    }

    [Fact]
    public void IndexAtOffset_WithNegativeOffset_ClampsToZero()
    {
        // 喵~防御：平移量为负数时不该反查出负数下标。
        Assert.Equal(0, CsgoTrack.IndexAtOffset(-999, SlotWidth, Gap, ViewportWidth));
    }

    #endregion

    #region 排片表

    [Fact]
    public void Build_WinnerEndsUpUnderThePointer()
    {
        // 排片表层面再验一遍：滚到终点时指针下的方块必须就是中选者。
        var plan = Build(["张三", "李四", "王五"], "王五");

        Assert.NotNull(plan);
        Assert.Equal(plan.WinnerTrackIndex,
            CsgoTrack.IndexAtOffset(plan.TargetOffset, plan.SlotWidth, plan.Gap, plan.ViewportWidth));
        Assert.Equal("王五", plan.Track[plan.WinnerTrackIndex]);
    }

    [Fact]
    public void Build_LeavesEnoughTravelBeforeTheWinner()
    {
        // 中选者要落在足够靠后的位置，不然「滚过去」的过程短得看不见。
        var plan = Build(["张三", "李四", "王五"], "张三");

        Assert.NotNull(plan);
        Assert.True(plan.WinnerTrackIndex >= CsgoTrack.MinTravelInSlots,
            $"中选者只在第 {plan.WinnerTrackIndex} 块，滚动过程太短");
    }

    [Fact]
    public void Build_TrackIsTheRosterRepeatedIntact()
    {
        // 轨道就是把名单一轮一轮地重复展开，顺序不能乱（重复的是同一批人，只有下标不同）。
        var names = new[] { "张三", "李四", "王五" };
        var plan = Build(names, "李四");

        Assert.NotNull(plan);
        for (var i = 0; i < plan.Track.Count; i++)
        {
            Assert.Equal(names[i % names.Length], plan.Track[i]);
        }
    }

    [Fact]
    public void Build_WithSingleName_StillProducesAPlayableTrack()
    {
        // 名单里只有一个人：轨道上全是同一个名字，照样能滚。
        var plan = Build(["张三"], "张三");

        Assert.NotNull(plan);
        Assert.All(plan.Track, name => Assert.Equal("张三", name));
    }

    [Fact]
    public void Build_TargetOffsetStaysWithinTheTrack()
    {
        // 滚过头会让指针下面出现空白，所以滚到哪都得留在轨道范围内。
        var plan = Build(["张三", "李四", "王五"], "王五");

        Assert.NotNull(plan);
        var trackLength = plan.Track.Count * (plan.SlotWidth + plan.Gap);
        Assert.True(plan.TargetOffset >= 0);
        Assert.True(plan.TargetOffset <= trackLength - plan.ViewportWidth + 0.001,
            $"平移量 {plan.TargetOffset} 超出了轨道可滚范围");
    }

    [Fact]
    public void Build_KeepsTheRequestedDuration()
    {
        // 排片表里记的时长要和传进来的配置一致。
        var plan = Build(["张三", "李四"], "张三");

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(4), plan.Total);
    }

    #endregion

    #region 边界与防御

    [Fact]
    public void Build_WithNullRoster_ReturnsNull()
    {
        Assert.Null(Build(null, "张三"));
    }

    [Fact]
    public void Build_WithEmptyRoster_ReturnsNull()
    {
        Assert.Null(Build([], "张三"));
    }

    [Fact]
    public void Build_WithNullWinner_ReturnsNull()
    {
        Assert.Null(Build(["张三", "李四"], null));
    }

    [Fact]
    public void Build_WhenWinnerNotInRoster_ReturnsNull()
    {
        // 动画开播前名单被改过：中选者已经不在了，绝不能停在一个不是他的方块上。
        Assert.Null(Build(["张三", "李四"], "查无此人"));
    }

    [Fact]
    public void Build_WithZeroSizedSlots_DoesNotThrow()
    {
        // 喵~防御：尺寸参数算错（这里是 0）时内部会夹到最小值，不能除零。
        var plan = CsgoTrack.Build(["张三", "李四"], "张三", 0, 0, 0, 0, 0, TimeSpan.FromSeconds(4));

        Assert.NotNull(plan);
        Assert.True(plan.SlotWidth > 0);
        Assert.True(plan.ViewportWidth > 0);
    }

    [Fact]
    public void Build_WithNaNSizes_DoesNotThrow()
    {
        // 喵~防御：尺寸变成 NaN 时同样要兜住，否则后面的取整全是垃圾值。
        var plan = CsgoTrack.Build(["张三", "李四"], "张三",
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, TimeSpan.FromSeconds(4));

        Assert.NotNull(plan);
        Assert.True(double.IsFinite(plan.TargetOffset));
    }

    #endregion
}
