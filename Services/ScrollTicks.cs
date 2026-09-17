using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 「滚动名字」样式：换帧时刻表，以及排片表的生成。
/// </summary>
/// <remarks>
/// 玩法是中央大字飞速换名字，换着换着越来越慢，最后停在中选者上。
/// <para/>
/// 「越来越慢」实现成<b>等比增长的间隔</b>：相邻两次换名字的间隔按固定倍数放大
/// （<see cref="GrowthRatio"/>，默认 1.12）。
/// <list type="bullet">
/// <item>开头几十毫秒就换一帧，看着就是一片残影；</item>
/// <item>中段变成几十上百毫秒，名字开始看得清；</item>
/// <item>最后两帧之间有近半秒，稳稳停住。</item>
/// </list>
/// <para/>
/// 为什么不用更容易想到的两条路：
/// <list type="bullet">
/// <item><b>等差数列</b>（间隔线性增长）：快慢变化太平缓，开头不够快、结尾不够慢，
///       缺少「刹车」的层次。</item>
/// <item><b>幂律时刻表</b>（第 i 帧在 总时长×(i/N)^k 那一刻出现）：看似更潮，
///       但 k=2 时相邻间隔之差是常数，<b>又退化成等差数列</b>；要真非线性就得取 k≥3，
///       而那会让开头几十帧全挤进几毫秒里，等于白演。</item>
/// </list>
/// 等比数列恰好避开了这两个坑。
/// <para/>
/// 时刻表离线算好（而不是每帧现乘一个系数），是为了让总时长精确可控：
/// 逐帧乘系数会带累积误差，配上「停留 X 秒」之后总时间就对不上了。
/// </remarks>
internal static class ScrollTicks
{
    /// <summary>相邻两帧间隔的增长倍数。大了转得太狠、小了又不够「刹车」。</summary>
    public const double GrowthRatio = 1.12;

    /// <summary>希望第一帧停留多久，单位：秒。用来反推帧数——够短才是残影而不是逐字念。</summary>
    public const double TargetFirstIntervalSeconds = 0.02;

    /// <summary>至少两帧，不然「滚动」无从谈起。</summary>
    public const int MinTicks = 2;

    /// <summary>帧数上限。总时长被拉到 20 秒时也不会算出上千帧。</summary>
    public const int MaxTicks = 240;

    /// <summary>
    /// 算出每一帧停留多久，单位：秒。
    /// </summary>
    /// <param name="totalSeconds">动画总时长，单位：秒。</param>
    /// <returns>每一帧的时长，长度就是帧数。各项之和等于总时长。</returns>
    /// <remarks>
    /// 间隔一定<b>严格递增</b>，这就是「先快后慢」在数字上的定义；
    /// 各项之和也一定<b>恰好等于</b>总时长，不然动画会比配置的长或短一截。
    /// 这两条都有单测钉着。
    /// </remarks>
    public static double[] BuildIntervals(double totalSeconds)
    {
        // 喵~防御：总时长不是有限正数（配置被改成 0、负数、NaN 或 Infinity）时，
        // 反推出来的帧数会是 0 或负数，后面取间隔就会越界。这里退化成「只有一帧」，
        // 效果等同于瞬间定格在中选者上，而不是崩溃或卡住。
        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0)
        {
            return [0];
        }

        // 帧数：让首项大致等于目标值。
        // 等比数列首项 = 总和 × (q-1) / (q^N - 1)，把它当成公式解出 N。
        var tickCount = (int)Math.Round(
            Math.Log(1 + totalSeconds * (GrowthRatio - 1) / TargetFirstIntervalSeconds) / Math.Log(GrowthRatio));
        tickCount = Math.Clamp(tickCount, MinTicks, MaxTicks);

        // 再用实际的帧数把首项精确解出来：这样各项之和<b>恰好</b>等于总时长，
        // 不会有四舍五入累计出来的「动画比配置长半秒」。
        var first = totalSeconds * (GrowthRatio - 1) / (Math.Pow(GrowthRatio, tickCount) - 1);

        // 逐帧填：每一项都是上一项乘公比。
        var intervals = new double[tickCount];
        var current = first;
        for (var i = 0; i < tickCount; i++)
        {
            intervals[i] = current;
            // 下一项按固定倍数放大。
            current *= GrowthRatio;
        }

        return intervals;
    }

    /// <summary>
    /// 生成「滚动名字」的排片表。
    /// </summary>
    /// <param name="names">当前名单，帧序就是它循环展开的一段。</param>
    /// <param name="winner">中选者，最后一帧一定是他。</param>
    /// <param name="totalSeconds">动画总时长，单位：秒。</param>
    public static ScrollPlan BuildPlan(IReadOnlyList<string>? names, string winner, double totalSeconds)
    {
        // 先算好每一帧停留多久。
        var intervals = BuildIntervals(totalSeconds);
        // 再按帧数排出每一帧要显示的名字。
        var frames = BuildFrames(names, winner, intervals.Length);
        // 总时长取各帧时长之和：这样「计划里写的时长」和「实际播的时长」永远一致。
        return new ScrollPlan(frames, intervals, TimeSpan.FromSeconds(intervals.Sum()), winner);
    }

    /// <summary>排出每一帧要显示的名字：名单循环展开的一段，最后一帧固定是中选者。</summary>
    /// <param name="names">当前名单。</param>
    /// <param name="winner">中选者。</param>
    /// <param name="frameCount">要排多少帧。</param>
    /// <remarks>
    /// <b>帧序就是名单本身循环展开的一段</b>，和 CSGO 开箱拼轨道用的是同一个思路：
    /// 名字一个挨一个滚过去，看着才像「滚动」。
    /// <para/>
    /// 早先的写法是每一帧从「其他人」里随机挑一个，名字在乱跳——
    /// 跳是跳得热闹，可减速感全被跳没了：换帧的间隔明明在等比变长，
    /// 画面却看不出节奏，只像「越来越慢地乱闪」。
    /// 2026-09-15 主人说的「他最后变慢的过程似乎消失了」以及
    /// 「参考 csgo 的滚动机制」，指的就是这里。
    /// <para/>
    /// 中选者排在最后一帧，他前面依次是名单里排在他前面的那些人——滚到他身上正好停住。
    /// </remarks>
    private static string[] BuildFrames(IReadOnlyList<string>? names, string winner, int frameCount)
    {
        // 喵~防御：一帧都不要时直接给空数组，下面的取模会除以 0。
        if (frameCount <= 0)
        {
            return [];
        }

        // 中选者在名单里的位置。
        var winnerIndex = -1;
        if (names is not null)
        {
            for (var i = 0; i < names.Count; i++)
            {
                // 用序数比较：名字是数据，不该受区域设置影响。
                if (string.Equals(names[i], winner, StringComparison.Ordinal))
                {
                    winnerIndex = i;
                    break;
                }
            }
        }

        var frames = new string[frameCount];

        // 喵~防御：名单为空、或者中选者不在名单里（名单在开播前被改过）时没有顺序可排，
        // 每一帧都显示中选者——效果等同于「定格在他身上」，而不是崩掉或者显示别人。
        if (names is null || names.Count == 0 || winnerIndex < 0)
        {
            for (var i = 0; i < frameCount; i++)
            {
                frames[i] = winner;
            }

            return frames;
        }

        for (var i = 0; i < frameCount; i++)
        {
            // 从末帧往前倒着数：末帧是中选者，往前一帧就是他名单里的前一个人，再往前再前一个……
            var offset = i - (frameCount - 1);
            var index = (winnerIndex + offset) % names.Count;
            // 喵~防御：C# 的取余对负数保留符号，负数下标会越界，再补一圈兜回 0~count-1。
            if (index < 0)
            {
                index += names.Count;
            }

            frames[i] = names[index];
        }

        // 末帧钉成中选者：上面那套算式本来就该给出他，这里再明写一次，免得取模写错时静默停错人。
        frames[frameCount - 1] = winner;
        return frames;
    }
}
