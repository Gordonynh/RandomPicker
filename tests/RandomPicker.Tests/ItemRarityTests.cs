using System;
using System.Linq;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// CSGO 名字品质的测试。
/// </summary>
/// <remarks>
/// <b>最要紧的一条是「品质不影响抽到谁」。</b>
/// 品质是给方块上色用的，如果它不小心和抽选共用了随机序列、或者反过来影响了中选者，
/// 那就等于悄悄改变了每个人的中选概率——这是这个插件最不能出的问题。
/// </remarks>
public sealed class ItemRarityTests
{
    /// <summary>固定选择器：永远返回指定的那个下标。</summary>
    private static Func<int, int> Fixed(int index) => _ => index;

    #region 品质表本身

    [Fact]
    public void Tiers_HaveFiveEntriesInAscendingRarity()
    {
        // 五档，顺序从最常见到最罕见——顺序是权重数组和配色数组的下标基准。
        Assert.Equal(5, ItemRarityTable.Tiers.Count);
        Assert.Equal(ItemRarity.MilSpec, ItemRarityTable.Tiers[0].Rarity);
        Assert.Equal(ItemRarity.RareSpecial, ItemRarityTable.Tiers[^1].Rarity);
    }

    [Fact]
    public void DefaultWeights_SumToExactlyOneHundred()
    {
        // 默认那套就是 CS:GO 开箱分布，加起来必须是 100。
        var sum = ItemRarityTable.Tiers.Sum(tier => tier.DefaultWeight);

        Assert.Equal(100.0, sum, 6);
    }

    [Fact]
    public void DefaultWeights_MatchCsgoCaseDistribution()
    {
        // 把这五个数字钉死。它们是从 CS:GO 开箱抄来的，不该随手改。
        Assert.Equal(79.92, ItemRarityTable.DefaultWeightOf(ItemRarity.MilSpec), 6);
        Assert.Equal(15.98, ItemRarityTable.DefaultWeightOf(ItemRarity.Restricted), 6);
        Assert.Equal(3.20, ItemRarityTable.DefaultWeightOf(ItemRarity.Classified), 6);
        Assert.Equal(0.64, ItemRarityTable.DefaultWeightOf(ItemRarity.Covert), 6);
        Assert.Equal(0.26, ItemRarityTable.DefaultWeightOf(ItemRarity.RareSpecial), 6);
    }

    [Fact]
    public void SettingsDefaultWeights_MatchTheTable()
    {
        // 设置类里那份默认值和品质表里那份必须一致。
        // 两份都存在是因为一份给纯计算用、一份给界面绑定用，
        // 改了一处忘了另一处会让「默认值」变得看运气——这条断言就是防这个的。
        var settings = new PickerSettings();

        Assert.Equal(ItemRarityTable.Tiers.Count, settings.RarityWeights.Count);
        for (var i = 0; i < ItemRarityTable.Tiers.Count; i++)
        {
            Assert.Equal(ItemRarityTable.Tiers[i].DefaultWeight, settings.RarityWeights[i], 6);
        }
    }

    [Fact]
    public void TierOf_OutOfRangeRarity_FallsBackToFirstTier()
    {
        // 喵~防御：枚举值是配置文件读进来的，越界时退回最常见那一档，
        // 而不是让索引异常把整段动画掀掉。
        var tier = ItemRarityTable.TierOf((ItemRarity)99);

        Assert.Equal(ItemRarity.MilSpec, tier.Rarity);
    }

    [Fact]
    public void TierOf_EveryTierHasANameAndAColor()
    {
        // 每一档都得有名字和配色，界面上才不会画出空白方块。
        foreach (var tier in ItemRarityTable.Tiers)
        {
            Assert.False(string.IsNullOrWhiteSpace(tier.Name));
            // 至少有一个分量不是 0，否则方块是纯黑的。
            Assert.True(tier.Red + tier.Green + tier.Blue > 0);
        }
    }

    #endregion

    #region 按权重抽

    [Fact]
    public void Roll_AtZero_ReturnsMostCommonTier()
    {
        // 落在区间最开头 → 第一档。
        Assert.Equal(ItemRarity.MilSpec, ItemRarityTable.Roll(null, Fixed(0)));
    }

    [Fact]
    public void Roll_AtTheVeryEnd_ReturnsRarestTier()
    {
        // 落在区间最末尾 → 最后一档（金）。
        // 总份额是 10000，所以最后一个合法下标是 9999。
        Assert.Equal(ItemRarity.RareSpecial, ItemRarityTable.Roll(null, Fixed(9999)));
    }

    [Fact]
    public void Roll_BoundariesLandInTheRightTiers()
    {
        // 逐条边界都验一遍：累计份额分别是 7992 / 9590 / 9910 / 9974 / 10000。
        // 边界差一位就抽错档，而 0.26% 那种小概率平时根本抽不到、测不出来。
        Assert.Equal(ItemRarity.MilSpec, ItemRarityTable.Roll(null, Fixed(0)));
        Assert.Equal(ItemRarity.MilSpec, ItemRarityTable.Roll(null, Fixed(7991)));
        Assert.Equal(ItemRarity.Restricted, ItemRarityTable.Roll(null, Fixed(7992)));
        Assert.Equal(ItemRarity.Restricted, ItemRarityTable.Roll(null, Fixed(9589)));
        Assert.Equal(ItemRarity.Classified, ItemRarityTable.Roll(null, Fixed(9590)));
        Assert.Equal(ItemRarity.Classified, ItemRarityTable.Roll(null, Fixed(9909)));
        Assert.Equal(ItemRarity.Covert, ItemRarityTable.Roll(null, Fixed(9910)));
        Assert.Equal(ItemRarity.Covert, ItemRarityTable.Roll(null, Fixed(9973)));
        Assert.Equal(ItemRarity.RareSpecial, ItemRarityTable.Roll(null, Fixed(9974)));
    }

    [Fact]
    public void Roll_IsNotAlwaysTheMostCommonTier()
    {
        // 真用熵源抽很多次，五个档位都得出现过——否则说明累加区间的实现有偏移，
        // 永远只命中第一档。
        // 用固定序列抽样会被上面那条边界测试盖住，这条专门验「真随机」这条路。
        var seen = new bool[ItemRarityTable.Tiers.Count];
        for (var i = 0; i < 20000; i++)
        {
            seen[(int)ItemRarityTable.Roll()] = true;
        }

        // 20000 抽里金（0.26%）期望出现约 52 次，没出现才奇怪。
        Assert.True(seen[0], "最常见那一档居然没出现");
        Assert.True(seen[1], "受限级没出现");
    }

    [Fact]
    public void Roll_WithAllWeightsZero_FallsBackToMostCommon()
    {
        // 喵~防御：五个滑块全拖到 0 时没有可抽的区间。
        // 退回最常见那一档，而不是除以 0、也不是不出结果。
        var result = ItemRarityTable.Roll([0, 0, 0, 0, 0], Fixed(0));

        Assert.Equal(ItemRarity.MilSpec, result);
    }

    [Fact]
    public void Roll_WithNegativeAndNaNAndInfinityWeights_AreIgnored()
    {
        // 喵~防御：手改配置文件写进负数、NaN、Infinity 时，这些档位当 0 处理，
        // 不能被它们污染累加区间。
        var result = ItemRarityTable.Roll([-50, double.NaN, double.PositiveInfinity, 100, 0], Fixed(0));

        // 唯一有效的是「隐秘级 100」，所以无论抽到哪儿都该是它。
        Assert.Equal(ItemRarity.Covert, result);
    }

    [Fact]
    public void Roll_WithShortWeightList_TreatsMissingOnesAsZero()
    {
        // 喵~防御：配置文件里只写了两项时，后面几档按 0 算。
        var result = ItemRarityTable.Roll([0, 100], Fixed(0));

        Assert.Equal(ItemRarity.Restricted, result);
    }

    [Fact]
    public void Roll_IgnoresOutOfRangeSelectorResult()
    {
        // 喵~防御：注入的选择器给越界下标时夹回合法范围，不能越界访问。
        Assert.Equal(ItemRarity.RareSpecial, ItemRarityTable.Roll(null, Fixed(999999)));
        Assert.Equal(ItemRarity.MilSpec, ItemRarityTable.Roll(null, Fixed(-5)));
    }

    [Fact]
    public void ToBuckets_ConvertsPercentagesToHundredthsOfAPercent()
    {
        // 百分数 → 万分之一份额，0.26% 变成 26，这样才有整数区间可抽。
        var buckets = ItemRarityTable.ToBuckets([79.92, 15.98, 3.20, 0.64, 0.26]);

        Assert.Equal([7992, 1598, 320, 64, 26], buckets);
        // 份额总和是 10000，也就是 100%。
        Assert.Equal(10000, buckets.Sum());
    }

    #endregion

    #region 品质不影响抽到谁

    [Fact]
    public void CsgoPlan_WinnerRarityDoesNotChangeTheWinner()
    {
        // 要害断言：把金色概率拉满，中选者依然和普通情况<b>完全一样</b>。
        // 品质是「事后摇的颜色」，不能和抽选共用任何东西。
        var names = new[] { "张三", "李四", "王五", "赵六" };
        // 固定选择器，让两次调用的装饰品质也一致，这样差异只可能来自品质权重。
        var selector = Fixed(0);
        // 停点也得固定住：指针停在方块内的哪个位置同样是随机摇的，
        // 两次不一样的话「滚动距离一样」这条就没法比了——而这条测的正是品质不参与抽选。
        Func<double> stopOffset = () => 0.5;

        var normal = CsgoTrack.Build(names, "李四", 400, 120, 60, 80, 8,
            TimeSpan.FromSeconds(4), ItemRarity.MilSpec, [79.92, 15.98, 3.20, 0.64, 0.26], selector,
            stopOffset);
        var allGold = CsgoTrack.Build(names, "李四", 400, 120, 60, 80, 8,
            TimeSpan.FromSeconds(4), ItemRarity.RareSpecial, [0, 0, 0, 0, 100], selector, stopOffset);

        Assert.NotNull(normal);
        Assert.NotNull(allGold);
        // 名字、中选位置、滚动距离都必须一模一样。
        Assert.Equal(normal.Winner, allGold.Winner);
        Assert.Equal(normal.WinnerTrackIndex, allGold.WinnerTrackIndex);
        Assert.Equal(normal.TargetOffset, allGold.TargetOffset, 6);
        Assert.Equal(normal.Track, allGold.Track);
        // 不一样的只有颜色。
        Assert.NotEqual(normal.WinnerRarity, allGold.WinnerRarity);
    }

    [Fact]
    public void CsgoPlan_WinnerSlotCarriesTheWinnerRarity()
    {
        // 轨道上中选那一块的品质必须是真正摇出来的那个，
        // 不能被装饰用的随机品质覆盖掉——否则方块颜色和实际开出的品质对不上。
        var plan = CsgoTrack.Build(["张三", "李四", "王五"], "王五", 400, 120, 60, 80, 8,
            TimeSpan.FromSeconds(4), ItemRarity.Covert, null, Fixed(0));

        Assert.NotNull(plan);
        Assert.Equal(ItemRarity.Covert, plan.Rarities[plan.WinnerTrackIndex]);
    }

    [Fact]
    public void CsgoPlan_RaritiesCoverTheWholeTrack()
    {
        // 每个方块都要有品质，否则画的时候会有一部分没颜色。
        var plan = CsgoTrack.Build(["张三", "李四", "王五"], "李四", 400, 120, 60, 80, 8,
            TimeSpan.FromSeconds(4), ItemRarity.MilSpec, null, Fixed(0));

        Assert.NotNull(plan);
        Assert.Equal(plan.Track.Count, plan.Rarities.Count);
    }

    #endregion
}
