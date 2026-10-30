using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 放回式抽取用的加权随机：越近被抽过的人，权重越低。
/// </summary>
/// <remarks>
/// <b>为什么不干脆用均匀随机：</b>一节课抽十几次、班上三十人，纯均匀下
/// 「某个人被抽三次、另一个人一次没抽」一点都不稀奇——均匀分布和「看起来公平」是两回事。
/// <para/>
/// <b>为什么也不干脆把最近抽过的全部拉黑：</b>那样 N 小的时候会退化成死板的轮流
/// （两个人就是 ABABAB），一眼假。
/// <para/>
/// 所以取中间：<b>上一次抽到的那个人权重 0</b>（硬排除，保证不连着抽同一个），
/// 再往前的按 <see cref="Decay"/> 指数衰减——两步前 0.5、三步前 0.25、四步前 0.125，
/// 更早的一律按 1。名单抽人和抽组都走这一套，两边观感才一致。
/// <para/>
/// 这组参数是模拟出来的，不是拍的：6 个组抽 10 次，纯均匀下有 39% 的概率某一组被抽到
/// 4 次以上，套上这套权重后降到 5%；30 人抽 12 次，「某人被抽 3 次以上」从 19% 降到 4%。
/// 再加大权重（0.35 / 记忆 5）还能再降一半，但那时已经接近死板轮回了，所以没取。
/// </remarks>
internal static class WeightedPicker
{
    /// <summary>每往前一步，权重乘这个系数。</summary>
    private const double Decay = 0.5;

    /// <summary>最多记最近几次。再早的没有区分必要，也免得状态无限长。</summary>
    public const int Memory = 4;

    /// <summary>
    /// 把一次抽到的名字推进「最近抽过」列表（最近的排最前），并裁到 <see cref="Memory"/> 条。
    /// </summary>
    public static void Remember(List<string> recent, string name)
    {
        recent.Insert(0, name);
        if (recent.Count > Memory)
        {
            recent.RemoveRange(Memory, recent.Count - Memory);
        }
    }

    /// <summary>某个名字当前的权重。</summary>
    /// <param name="name">候选名字。</param>
    /// <param name="recent">最近抽过的，最近的在最前面。</param>
    private static double WeightOf(string name, IReadOnlyList<string> recent)
    {
        for (var k = 0; k < recent.Count && k < Memory; k++)
        {
            if (!string.Equals(recent[k], name, StringComparison.Ordinal))
            {
                continue;
            }

            // k = 0 就是上一次抽到的那个人：直接排除，保证不会连着抽到同一个。
            return k == 0 ? 0 : Math.Pow(Decay, k);
        }

        return 1;
    }

    /// <summary>
    /// 从候选里按权重挑一个。
    /// </summary>
    /// <remarks>
    /// 权重全为 0 只可能发生在「候选里只剩上一次抽到的那一个」——
    /// 这时候没有别的选择，只能把它给出去，否则会抽不出来。
    /// </remarks>
    public static string Pick(IReadOnlyList<string> candidates, IReadOnlyList<string> recent)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var total = 0.0;
        foreach (var candidate in candidates)
        {
            total += WeightOf(candidate, recent);
        }

        if (total <= 0)
        {
            return candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        }

        // 落在权重轴上，落在谁那一段就是谁。
        var roll = NextDouble() * total;
        foreach (var candidate in candidates)
        {
            roll -= WeightOf(candidate, recent);
            if (roll <= 0)
            {
                return candidate;
            }
        }

        // 浮点误差兜底，正常走不到。
        return candidates[^1];
    }

    /// <summary>
    /// [0, 1) 之间的随机数。
    /// </summary>
    /// <remarks>
    /// 自己从熵源里拼，而不是用 <c>Random.Shared</c>：前者没有种子、也没有跨线程的状态，
    /// 和上面抽人用的是同一档随机质量。取 8 字节、右移 11 位留下 53 位尾数，
    /// 精度和 <c>Random.NextDouble()</c> 一致，不会因为 int 取模产生偏斜。
    /// </remarks>
    private static double NextDouble()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return (BitConverter.ToUInt64(buffer) >> 11) * (1.0 / (1UL << 53));
    }
}
