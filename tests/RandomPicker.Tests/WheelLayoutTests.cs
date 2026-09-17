using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 转盘样式的几何测试。
/// </summary>
/// <remarks>
/// 「指针停在两个扇区的边界上，再滑进中选那一格」这段几何是整个动画里最容易写反的地方：
/// 方向反了会停在<b>中选者旁边那一格</b>——肉眼看得出来，但只在部分扇区数下出错，很难复现。
/// 所以这里不做抽样，而是把「扇区数 2~12 × 每个中选位 × 两种边界」全遍历一遍。
/// </remarks>
public sealed class WheelLayoutTests
{
    /// <summary>一个够大的名单，用来撑满 12 个扇区。</summary>
    private static readonly string[] BigRoster =
        ["张三", "李四", "王五", "赵六", "钱七", "孙八", "周九", "吴十", "郑一", "王二", "冯三", "陈四"];

    /// <summary>固定选择器：永远挑第一个，让结果可以断言。</summary>
    private static Func<int, int> AlwaysFirst => _ => 0;

    #region 核心恒等式：滑完之后指针必须落在中选者身上

    [Fact]
    public void AngleAfterSlide_AlwaysPutsWinnerUnderPointer()
    {
        // 遍历所有可能的扇区数。
        for (var count = 2; count <= WheelLayout.MaxSectors; count++)
        {
            // 遍历中选者可能待的每个扇区。
            for (var winnerSector = 0; winnerSector < count; winnerSector++)
            {
                // 两条边界都要试：一条是中选者左边那条（停在它上面就往右滑回来），
                // 一条是右边那条（停在它上面就往左滑过来）。
                foreach (var boundaryIndex in new[] { winnerSector, (winnerSector + 1) % count })
                {
                    // 方向按规则推：边界正好是中选者左边界就往 -1 滑，否则 +1。
                    var direction = boundaryIndex == winnerSector ? -1 : 1;
                    // 滑完之后指针下的扇区。
                    var landed = WheelLayout.SectorUnderPointer(
                        WheelLayout.AngleAfterSlide(boundaryIndex, count, direction), count);

                    // 必须是中选者那一格——错了就说明方向公式被改坏了。
                    Assert.Equal(winnerSector, landed);
                }
            }
        }
    }

    [Fact]
    public void AngleAfterSlide_IsExactlyHalfSectorFromBoundary()
    {
        // 滑进扇区只挪半格，挪多了或少了都会让指针压在别的格上。
        for (var count = 2; count <= WheelLayout.MaxSectors; count++)
        {
            var sweep = 360.0 / count;
            var boundary = WheelLayout.AngleAtBoundary(0, count);
            var afterSlide = WheelLayout.AngleAfterSlide(0, count, 1);
            Assert.Equal(sweep / 2, afterSlide - boundary, 6);
        }
    }

    [Fact]
    public void AngleAtBoundary_TurnsAtLeastTheConfiguredRounds()
    {
        // 空转的圈数要足，不然「转起来」的感觉就没了。
        // 角度是负的（顺时针转），所以绝对值至少要等于整圈数。
        for (var count = 2; count <= WheelLayout.MaxSectors; count++)
        {
            var angle = WheelLayout.AngleAtBoundary(0, count);
            Assert.True(angle <= -360.0 * WheelLayout.Turns,
                $"{count} 个扇区时只转了 {-angle / 360.0:F2} 圈");
        }
    }

    [Fact]
    public void SectorUnderPointer_AtExactSectorCenter_IsStable()
    {
        // 指针正好压在某个扇区中心时不能因为浮点误差判到隔壁去。
        for (var count = 2; count <= WheelLayout.MaxSectors; count++)
        {
            var sweep = 360.0 / count;
            for (var sector = 0; sector < count; sector++)
            {
                // 该扇区的中心角。
                var centerAngle = -(sector + 0.5) * sweep;
                Assert.Equal(sector, WheelLayout.SectorUnderPointer(centerAngle, count));
            }
        }
    }

    [Fact]
    public void SectorUnderPointer_AtBoundary_FallsIntoTheForwardSector()
    {
        // 正好压在边界上时归哪一格？约定是归「靠后一格」，
        // 和判定条件 -(i+1)·sweep < θ <= -i·sweep 的右端闭合一致。
        const int count = 4;
        // 第 1 条边界在 -90 度。
        Assert.Equal(1, WheelLayout.SectorUnderPointer(-90, count));
    }

    [Fact]
    public void SectorUnderPointer_WithZeroSectors_ReturnsZero()
    {
        // 喵~防御：扇区数为 0 时内部会除零，必须给个安全值。
        Assert.Equal(0, WheelLayout.SectorUnderPointer(-123.0, 0));
    }

    [Fact]
    public void SectorUnderPointer_WithNonFiniteAngle_ReturnsZero()
    {
        // 配置被改成 NaN 时角度可能是 NaN，不能让它传播成越界下标。
        Assert.Equal(0, WheelLayout.SectorUnderPointer(double.NaN, 6));
    }

    #endregion

    #region 排片表

    [Fact]
    public void Build_PutsWinnerUnderPointer()
    {
        // 排片表层面也要验一遍：最终角度必须指向中选者在盘面上的那一格。
        var plan = WheelLayout.Build(BigRoster, "王五", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(plan.WinnerSector,
            WheelLayout.SectorUnderPointer(plan.FinalAngle, plan.Sectors.Count));
    }

    [Fact]
    public void Build_CapsSectorCountAtTwelve()
    {
        // 二十个人的名单，盘面最多只放 12 个。
        var roster = Enumerable.Range(1, 20).Select(index => $"学生{index}").ToArray();

        var plan = WheelLayout.Build(roster, "学生7", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(WheelLayout.MaxSectors, plan.Sectors.Count);
    }

    [Fact]
    public void Build_WhenRosterIsSmallerThanMax_DoesNotRepeatNames()
    {
        // 五个人的名单：画 5 个扇区，不靠重复来凑满。
        var roster = new[] { "张三", "李四", "王五", "赵六", "钱七" };

        var plan = WheelLayout.Build(roster, "张三", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(5, plan.Sectors.Count);
        // 每个人的名字只出现一次。
        Assert.Equal(5, plan.Sectors.Distinct().Count());
    }

    [Fact]
    public void Build_SectorsAreWhatTheRosterLooksLike()
    {
        // 扇区上的名字必须都来自名单，不能凭空冒出来。
        var roster = new[] { "张三", "李四", "王五" };

        var plan = WheelLayout.Build(roster, "王五", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.All(plan.Sectors, sector => Assert.Contains(sector, roster));
    }

    [Fact]
    public void Build_WinnerIsAlwaysOnTheWheel()
    {
        // 中选者不在盘上的话，指针停下来会指着一个不是他名字的格子。
        var plan = WheelLayout.Build(BigRoster, "周九", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Contains("周九", plan.Sectors);
        Assert.Equal("周九", plan.Sectors[plan.WinnerSector]);
    }

    [Fact]
    public void Build_BothSlideDirectionsAreReachable()
    {
        // 50/50 的两条路都要能走到：选择器给 0 时和给 1 时，
        // 停的边界不同，但最终都会滑进中选者那一格。
        var roster = new[] { "张三", "李四", "王五", "赵六" };

        // 选择器固定给 0。
        var first = WheelLayout.Build(roster, "张三", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, _ => 0);
        // 选择器固定给 1（用来抽人的时候夹到最后一个，用来掷硬币时选另一面）。
        var second = WheelLayout.Build(roster, "张三", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, _ => 1);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // 两种情况下最终都指着中选者。
        Assert.Equal(first.WinnerSector, WheelLayout.SectorUnderPointer(first.FinalAngle, first.Sectors.Count));
        Assert.Equal(second.WinnerSector, WheelLayout.SectorUnderPointer(second.FinalAngle, second.Sectors.Count));
        // 滑入方向都是合法的 -1 或 +1。
        Assert.Contains(first.SlideDirection, new[] { -1, 1 });
        Assert.Contains(second.SlideDirection, new[] { -1, 1 });
    }

    [Fact]
    public void Build_TotalIsHoldPlusSlide()
    {
        // 总时长 = 转到边界 + 滑进扇区，配置只管前面那一段。
        var plan = WheelLayout.Build(BigRoster, "张三", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(6), plan.HoldDuration);
        Assert.Equal(WheelLayout.SlideDuration, plan.SlideDuration);
        Assert.Equal(TimeSpan.FromSeconds(6) + WheelLayout.SlideDuration, plan.Total);
    }

    #endregion

    #region 边界与防御

    [Fact]
    public void Build_WithNullRoster_ReturnsNull()
    {
        Assert.Null(WheelLayout.Build(null, "张三", TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Build_WithEmptyRoster_ReturnsNull()
    {
        Assert.Null(WheelLayout.Build([], "张三", TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Build_WithSingleName_ReturnsNull()
    {
        // 只有一个人时转盘没有意义（扇区数不足 2），交给上层降级成不播动画。
        Assert.Null(WheelLayout.Build(["张三"], "张三", TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Build_WithNullWinner_ReturnsNull()
    {
        Assert.Null(WheelLayout.Build(BigRoster, null, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Build_WhenWinnerNotInRoster_ReturnsNull()
    {
        // 动画开播前名单被改过：中选者已经不在名单里了，绝不能指错人。
        Assert.Null(WheelLayout.Build(BigRoster, "查无此人", TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Build_WithZeroHoldDuration_UsesFallback()
    {
        // 喵~防御：时长被配置改成 0 时不能一闪而过，要兜底成 1 秒。
        var plan = WheelLayout.Build(BigRoster, "张三", TimeSpan.Zero, WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.HoldDuration);
    }

    [Fact]
    public void Build_WithTwoNames_ProducesTwoSectors()
    {
        // 只有两个人时是 2 个 180 度的扇区，这时扇形正好是半个圆（IsLargeArc 该是 false）。
        var plan = WheelLayout.Build(["张三", "李四"], "李四", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Sectors.Count);
        Assert.Equal(plan.WinnerSector, WheelLayout.SectorUnderPointer(plan.FinalAngle, 2));
    }

    [Fact]
    public void Build_WithMaxSectorsBelowTwo_ClampsToTwo()
    {
        // 调用方传了个荒谬的上限，不能让它算出 1 个扇区。
        var plan = WheelLayout.Build(["张三", "李四", "王五"], "张三",
            TimeSpan.FromSeconds(6), maxSectors: 1, AlwaysFirst);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Sectors.Count);
    }

    [Fact]
    public void Build_WithOutOfRangeSelector_DoesNotThrow()
    {
        // 喵~防御：注入的选择器给出越界下标时，内部会夹回合法范围。
        var plan = WheelLayout.Build(BigRoster, "张三", TimeSpan.FromSeconds(6), WheelLayout.MaxSectors, _ => 999);

        Assert.NotNull(plan);
        Assert.Equal(plan.WinnerSector, WheelLayout.SectorUnderPointer(plan.FinalAngle, plan.Sectors.Count));
    }

    #endregion
}
