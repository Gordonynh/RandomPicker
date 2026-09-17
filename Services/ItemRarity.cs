using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 饰品品质档位。
/// </summary>
/// <remarks>
/// 照搬 CS:GO 开箱那五档，名字和配色都用官方那一套，抽过箱子的人一眼就认得。
/// </remarks>
internal enum ItemRarity
{
    /// <summary>军规级（蓝）。最常见的一档。</summary>
    MilSpec,

    /// <summary>受限级（紫）。</summary>
    Restricted,

    /// <summary>保密级（粉）。</summary>
    Classified,

    /// <summary>隐秘级（红）。</summary>
    Covert,

    /// <summary>罕见特殊物品（金）。开箱里的头奖，出一次要三百多抽。</summary>
    RareSpecial
}

/// <summary>
/// 一档品质的完整信息。
/// </summary>
/// <param name="Rarity">档位。</param>
/// <param name="Name">显示名，比如「隐秘级」。</param>
/// <param name="Red">配色的红色分量。</param>
/// <param name="Green">配色的绿色分量。</param>
/// <param name="Blue">配色的蓝色分量。</param>
/// <param name="DefaultWeight">默认权重（百分数），五个加起来是 100。</param>
/// <remarks>
/// 颜色存成三个字节而不是 <c>Avalonia.Media.Color</c>：
/// 这一层是纯计算，不引用界面框架，才好离线跑单元测试。
/// 界面那边自己把字节拼成 <c>Color</c>。
/// </remarks>
internal readonly record struct RarityTier(ItemRarity Rarity, string Name,
    byte Red, byte Green, byte Blue, double DefaultWeight);

/// <summary>
/// 品质表：五个档位的名字、配色、默认权重，以及按权重抽档位。
/// </summary>
/// <remarks>
/// <b>这套东西只影响动画表演，完全不影响抽到谁。</b>
/// 中选者在更早的一步就已经定好了，品质是在那之后单独摇的——两者互相独立。
/// 换句话说，把金色的概率调到 100% 也不会让任何人更容易被抽中。
/// </remarks>
internal static class ItemRarityTable
{
    /// <summary>五个档位，从最常见到最罕见。</summary>
    /// <remarks>
    /// 默认权重就是 CS:GO 开箱那套经典分布，加起来正好 100。
    /// 金的 0.26% 意味着平均 385 抽才见一次。
    /// </remarks>
    public static IReadOnlyList<RarityTier> Tiers { get; } =
    [
        new(ItemRarity.MilSpec, "军规级", 0x4B, 0x69, 0xFF, 79.92),
        new(ItemRarity.Restricted, "受限级", 0x88, 0x47, 0xFF, 15.98),
        new(ItemRarity.Classified, "保密级", 0xD3, 0x2C, 0xE6, 3.20),
        new(ItemRarity.Covert, "隐秘级", 0xEB, 0x4B, 0x4B, 0.64),
        new(ItemRarity.RareSpecial, "罕见特殊物品", 0xFF, 0xD7, 0x00, 0.26)
    ];

    /// <summary>按档位取完整信息。</summary>
    /// <remarks>
    /// 喵~防御：枚举值是外部（配置文件）读进来的，越界时退回最常见那一档，
    /// 而不是让索引异常把整段动画掀掉。
    /// </remarks>
    public static RarityTier TierOf(ItemRarity rarity)
    {
        // 喵~防御：枚举值是外部（配置文件）读进来的，越界时退回最常见那一档，
        // 而不是让索引异常把整段动画掀掉。
        var index = (int)rarity;
        if (index < 0 || index >= Tiers.Count)
        {
            return Tiers[0];
        }

        return Tiers[index];
    }

    /// <summary>某一档的默认权重。</summary>
    public static double DefaultWeightOf(ItemRarity rarity) => TierOf(rarity).DefaultWeight;

    /// <summary>把权重百分数换算成整数份额，好按整数抽。</summary>
    /// <param name="weights">各档权重（百分数），长度不足时后面几档按 0 算。</param>
    /// <returns>每档占的份额，单位是万分之一。</returns>
    /// <remarks>
    /// 换成整数是为了避免浮点误差：0.26% 这种小概率用浮点比较很容易抽不到或者抽过头，
    /// 乘 100 取整之后就是干净的整数区间抽取。
    /// </remarks>
    public static int[] ToBuckets(IReadOnlyList<double>? weights)
    {
        var buckets = new int[Tiers.Count];
        for (var i = 0; i < buckets.Length; i++)
        {
            // 没给的档位按 0 算。
            var weight = weights is not null && i < weights.Count ? weights[i] : 0;
            // 喵~防御：负数、NaN、Infinity 一律当 0——用户可能把滑块拖到坏值上，
            // 或者手改配置文件写了个奇怪的东西。份额不能是负的，否则累加区间会错乱。
            if (!double.IsFinite(weight) || weight <= 0)
            {
                continue;
            }

            // 百分数 → 万分之一，四舍五入。
            buckets[i] = (int)Math.Round(weight * 100);
        }

        return buckets;
    }

    /// <summary>
    /// 按权重抽一档品质。
    /// </summary>
    /// <param name="weights">各档权重（百分数）。传 <c>null</c> 用默认那套。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到「总份额减一」里挑一个下标的函数。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>抽到的档位。</returns>
    public static ItemRarity Roll(IReadOnlyList<double>? weights = null,
        Func<int, int>? pickIndexSelector = null)
    {
        // 没给权重就用默认那套。
        var buckets = ToBuckets(weights ?? Tiers.Select(tier => tier.DefaultWeight).ToArray());

        var total = 0;
        foreach (var bucket in buckets)
        {
            total += bucket;
        }

        // 喵~防御：份额全是 0（用户把五个滑块都拖到了 0）时没有可抽的区间。
        // 退回最常见那一档——总不能因为概率配得离谱就不出结果。
        if (total <= 0)
        {
            return ItemRarity.MilSpec;
        }

        // 在 [0, 总份额) 里取一个整数。
        var pick = pickIndexSelector?.Invoke(total) ?? RandomNumberGenerator.GetInt32(total);
        // 喵~防御：注入的选择器可能给出越界下标，夹回合法范围再用。
        pick = Math.Clamp(pick, 0, total - 1);

        // 累加区间，看落进哪一档。
        var accumulated = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            accumulated += buckets[i];
            if (pick < accumulated)
            {
                return Tiers[i].Rarity;
            }
        }

        // 理论上到不了这儿（上面已经夹过范围），兜底给最常见那一档。
        return ItemRarity.MilSpec;
    }
}
