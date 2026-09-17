using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 「CSGO 开箱」样式：方块轨道的排布与滚动距离。
/// </summary>
/// <remarks>
/// 玩法是一排名字方块横向飞快滚过，中间有一根指针，最后减速停在指针下面的那一块上。
/// 做法是把名单重复展开好几轮拼成一条长轨道，让中选者出现在靠后的某一轮里，
/// 再滚动一段算好的距离，使<b>那一块的中心正好落在视口正中</b>。
/// <para/>
/// 这里的几何是精确的、不是近似的：滚到终点后，指针下的方块必然是中选者那一个，
/// 这一点由单元测试逐项钉住（<see cref="IndexAtOffset"/> 是 <see cref="OffsetFor"/> 的反函数）。
/// </remarks>
internal static class CsgoTrack
{
    /// <summary>中选者前面至少要留出这么多个方块的距离，才有足够的滚动过程可看。</summary>
    public const double MinTravelInSlots = 20.0;

    /// <summary>名单至少要重复这么多轮，人少的名单才不会一屏就滚完。</summary>
    public const int MinRepeats = 3;

    /// <summary>
    /// 停点相对方块中心最多偏多少，单位：方块宽度的比例。
    /// </summary>
    /// <remarks>
    /// <b>指针不是每次都停在方块正中的。</b>真实开箱里滚动是连续刹停的，
    /// 指针落在方块内的哪个位置基本是随机的，每次都正中对齐反而一眼假。
    /// 上限定在 45% 而不是 50%，是留一点余量：偏移正好顶到半个方块宽时，
    /// <see cref="IndexAtOffset"/> 的圆整有可能把指针算到隔壁那一块上去，
    /// 那就成了「指针旁边那个名字被高亮」——最伤信任的一类错误。
    /// </remarks>
    public const double MaxStopJitterRatio = 0.45;

    /// <summary>
    /// 生成 CSGO 开箱的排片表。
    /// </summary>
    /// <param name="names">当前名单。</param>
    /// <param name="winner">中选者。</param>
    /// <param name="viewportWidth">可视区宽度，单位：逻辑像素。</param>
    /// <param name="viewportHeight">可视区高度，单位：逻辑像素。</param>
    /// <param name="slotWidth">一个方块的宽度。</param>
    /// <param name="slotHeight">一个方块的高度。</param>
    /// <param name="gap">方块之间的间距。</param>
    /// <param name="total">动画总时长。</param>
    /// <param name="winnerRarity">中选者的品质。只影响表演，不影响抽到谁。</param>
    /// <param name="rarityWeights">各档品质的权重（百分数）。传 <c>null</c> 用默认那套。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到 n-1 里挑一个下标的函数，用来给轨道上<b>其他</b>方块摇装饰用的品质。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <param name="stopOffsetSelector">
    /// 返回一个 0~1 的随机数，用来决定指针停在方块内的哪个位置。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>排片表；名单不合法或参数病态时返回 <c>null</c>（上层据此不播动画）。</returns>
    public static CsgoPlan? Build(IReadOnlyList<string>? names, string? winner,
        double viewportWidth, double viewportHeight,
        double slotWidth, double slotHeight, double gap, TimeSpan total,
        ItemRarity winnerRarity = ItemRarity.MilSpec,
        IReadOnlyList<double>? rarityWeights = null,
        Func<int, int>? pickIndexSelector = null,
        Func<double>? stopOffsetSelector = null)
    {
        // 喵~防御：名单为空或中选者为空时没什么可滚的，直接作废这段动画。
        if (names is null || names.Count == 0 || string.IsNullOrEmpty(winner))
        {
            return null;
        }

        // 找出中选者在名单里的位置。
        var winnerIndex = -1;
        for (var i = 0; i < names.Count; i++)
        {
            // 用序数比较：名字是数据，不该受区域设置影响。
            if (string.Equals(names[i], winner, StringComparison.Ordinal))
            {
                winnerIndex = i;
                break;
            }
        }

        // 喵~防御：中选者不在名单里（动画开播前名单被改过）→ 作废，宁可不出动画也不能停错人。
        if (winnerIndex < 0)
        {
            return null;
        }

        // 喵~防御：尺寸参数算错（0、负数、NaN）时，后面的除法和取整会算出 NaN。
        // 这里统一夹到最小值，保证下面所有几何计算都在有限值上进行。
        var safeSlotWidth = double.IsFinite(slotWidth) && slotWidth > 0 ? slotWidth : 1;
        var safeSlotHeight = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 1;
        var safeGap = double.IsFinite(gap) && gap >= 0 ? gap : 0;
        var safeViewportWidth = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1;
        var safeViewportHeight = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 1;
        // 相邻两个方块中心的距离。因为 safeSlotWidth 至少是 1，这个值不可能为 0，下面不用担心除零。
        var pitch = safeSlotWidth + safeGap;

        // 中选者前面至少要留出这么多方块的距离：MinTravelInSlots 个方块，再加上半个视口，
        // 这样指针是「从右边稳稳滑进来」的，而不是一开始就停在他身上。
        var minTrackIndex = MinTravelInSlots + safeViewportWidth / (2 * pitch);
        // 把中选者放到某一轮里「他自己那个位置」上，并且往前推到第一个满足距离要求的整轮位置——
        // 这样它前面的名字序列和名单本身一致，看起来才像一回事。
        var winnerTrackIndex = (int)Math.Ceiling(minTrackIndex / names.Count) * names.Count + winnerIndex;

        // 中选者后面也得留够方块，否则滚到终点时视口的右半边是空的。
        // 半个视口足够放下中选者本身，再多留一块，免得正好卡在边界上。
        var tailNeeded = (int)Math.Ceiling((safeViewportWidth / 2 + safeSlotWidth / 2) / pitch) + 1;
        // 轨道至少要这么长，再向上取整到整轮。
        var neededLength = winnerTrackIndex + tailNeeded;
        var repeats = (int)Math.Ceiling(neededLength / (double)names.Count);
        // 至少重复 MinRepeats 轮——名单只有两三个人时，太少轮会一屏就滚完，没有「飞快滚过」的感觉。
        repeats = Math.Max(MinRepeats, repeats);

        // 把名单重复展开成轨道。
        var trackLength = repeats * names.Count;
        var track = new string[trackLength];
        // 每个方块对应的品质：中选那块用真正摇出来的，其余只是装饰。
        var rarities = new ItemRarity[trackLength];
        for (var i = 0; i < trackLength; i++)
        {
            // 按轮次循环取名字。
            track[i] = names[i % names.Count];
            // 喵~防御：每个方块都要有一个品质，装饰块也得摇一次。
            // 摇出来的只是颜色，和「抽到谁」完全无关。
            rarities[i] = ItemRarityTable.Roll(rarityWeights, pickIndexSelector);
        }

        // 中选那块覆盖成真正摇出来的品质——它是唯一有意义的那个，
        // 其余方块的品质纯粹是滚动过程中闪过去的颜色。
        rarities[winnerTrackIndex] = winnerRarity;

        // 指针停在方块内的哪个位置：先摇一个 0~1 的数，再摊成 -1~1 的偏移比例。
        // 生产环境用操作系统熵源，测试注入固定函数。
        var roll = stopOffsetSelector?.Invoke()
                   ?? RandomNumberGenerator.GetInt32(1 << 20) / (double)(1 << 20);
        // 喵~防御：注入的选择器可能给出 NaN 或越界值，那样算出来的平移量会是 NaN，
        // 画面整个不见。坏值一律按「停在正中间」处理。
        if (!double.IsFinite(roll))
        {
            roll = 0.5;
        }

        // 夹到 [0, 1)：乘 2 减 1 之后正好是 [-1, 1) 的偏移比例。
        roll = Math.Clamp(roll, 0.0, 0.999999);

        // 让那一块停在视口正中所需的平移量，再加上随机偏移——指针因此落在方块内的任意位置，
        // 而不是每次都压在名字正中。偏移量不超过 MaxStopJitterRatio 个方块宽，
        // 指针底下因此仍然是中选者那一块（后面还有一道反查断言兜着）。
        var targetOffset = OffsetFor(winnerTrackIndex, safeSlotWidth, safeGap, safeViewportWidth)
                           + (roll * 2 - 1) * safeSlotWidth * MaxStopJitterRatio;
        // 轨道末端最多能滚到哪里：再往后滚指针下面就空了。
        var maxOffset = trackLength * pitch - safeViewportWidth;

        // 喵~防御：轨道短到装不满一屏时（尺寸参数病态），怎么滚都到不了中选者身上。
        // 与其停在一个不是中选者的方块上，不如干脆不播这段动画，静默降级成无动画。
        if (maxOffset < 0)
        {
            return null;
        }

        // 把平移量夹进合法区间。
        targetOffset = Math.Clamp(targetOffset, 0, maxOffset);

        // 喵~防御：夹取之后指针下的方块必须仍然是中选者。
        // 不满足就说明参数病态，同样作废——绝不能演出一个人人都看得出来的错误结果。
        if (IndexAtOffset(targetOffset, safeSlotWidth, safeGap, safeViewportWidth) != winnerTrackIndex)
        {
            return null;
        }

        return new CsgoPlan(track, winnerTrackIndex,
            safeSlotWidth, safeSlotHeight, safeGap,
            safeViewportWidth, safeViewportHeight,
            targetOffset, rarities, winnerRarity, total, winner);
    }

    /// <summary>
    /// 让第 <paramref name="trackIndex"/> 个方块的中心正好落在视口正中所需的平移量。
    /// </summary>
    /// <param name="trackIndex">方块在轨道里的下标。</param>
    /// <param name="slotWidth">方块宽度，单位：逻辑像素。</param>
    /// <param name="gap">方块间距，单位：逻辑像素。</param>
    /// <param name="viewportWidth">视口宽度，单位：逻辑像素。</param>
    public static double OffsetFor(int trackIndex, double slotWidth, double gap, double viewportWidth)
    {
        // 这个方块左边界在轨道坐标系里的位置。
        var slotLeft = trackIndex * (slotWidth + gap);
        // 让它的中心对齐视口中心，就是需要的平移量。
        return slotLeft + slotWidth / 2 - viewportWidth / 2;
    }

    /// <summary>
    /// 滚动到指定平移量时，指针（固定在视口正中）下方是第几个方块。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="OffsetFor"/> 的反函数，滚到终点后拿它反查下标必须得到中选者，
    /// 单元测试就是靠这条恒等式把「停错人」这种 bug 挡住的。
    /// </remarks>
    public static int IndexAtOffset(double offset, double slotWidth, double gap, double viewportWidth)
    {
        // 相邻方块中心的间距。宽度至少 1，所以不会除零。
        var pitch = slotWidth + gap;
        // 视口正中落在轨道坐标系的哪个位置，再换算成「第几个方块」。
        var center = offset + viewportWidth / 2;
        // 圆整到最近的方块下标；负数（滚过头了）夹到 0。
        return Math.Max(0, (int)Math.Round((center - slotWidth / 2) / pitch));
    }
}
