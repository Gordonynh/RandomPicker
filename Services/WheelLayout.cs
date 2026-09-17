using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 「拼多多转盘」样式：扇区选取、指针停在边界、再滑进中选那一格。
/// </summary>
/// <remarks>
/// 玩法是指针先空转几圈，减速停到<b>两个扇区之间的缝隙</b>上，晃一下，
/// 然后往左或往右挪半格滑进当中一格——就是中选者。
/// <para/>
/// <b>这里最容易被写反的一段：</b>扇区 <c>i</c> 在轮盘本地坐标里占
/// <c>[i·sweep, (i+1)·sweep)</c>，<c>sweep = 360/count</c>，0 度是正上方。
/// 轮盘转 <c>θ</c> 之后，指针（固定在正上方）指着扇区 <c>i</c> 的充要条件是
/// <c>-(i+1)·sweep &lt; θ &lt;= -i·sweep</c>，扇区中心角是 <c>-(i+0.5)·sweep</c>。
/// <para/>
/// 于是「停边界再滑进去」有两条路，正好就是那个 50/50：
/// <code>
/// b         = 随机取 w 或 (w+1) % count     // 停在哪条边界上
/// direction = (b == w) ? -1 : +1            // 从哪一侧滑进去
/// θ边界     = -(360·Turns) - b·sweep
/// θ最终     = θ边界 + direction · sweep/2
/// </code>
/// 两条路算出的 <c>θ最终</c> 归一化之后<b>完全相等</b>，都等于中选扇区的中心角。
/// 换句话说，50/50 决定的不是最终停在哪（那是确定的），而是<b>从哪一侧滑进去</b>——
/// 视觉上就是指针停在缝上晃一下，再往左或往右挪半格。
/// </remarks>
internal static class WheelLayout
{
    /// <summary>扇区数上限。再多名字就挤得看不清了。</summary>
    public const int MaxSectors = 12;

    /// <summary>指针在停到边界之前先空转几圈。</summary>
    public const int Turns = 4;

    /// <summary>从边界滑进扇区固定花多久。这一段不随配置变，短促才像「滑」。</summary>
    public static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(0.35);

    /// <summary>
    /// 生成转盘的排片表。
    /// </summary>
    /// <param name="names">当前名单。</param>
    /// <param name="winner">中选者，一定会出现在盘面上。</param>
    /// <param name="holdDuration">转到边界用多久。</param>
    /// <param name="maxSectors">最多画几个扇区。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到 n-1 里挑一个下标的函数（用来抽扇区、洗牌，以及决定往哪一侧滑）。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>排片表；名单不合法时返回 <c>null</c>（上层据此不播动画）。</returns>
    public static WheelPlan? Build(IReadOnlyList<string>? names, string? winner, TimeSpan holdDuration,
        int maxSectors = MaxSectors, Func<int, int>? pickIndexSelector = null)
    {
        // 喵~防御：名单为空或没有中选者时没什么可转的。
        if (names is null || names.Count == 0 || string.IsNullOrEmpty(winner))
        {
            return null;
        }

        // 喵~防御：中选者不在名单里（动画开播前名单被改过）→ 作废，宁可不出动画也不能指错人。
        if (!names.Any(name => string.Equals(name, winner, StringComparison.Ordinal)))
        {
            return null;
        }

        // 扇区上限夹到合法区间：至少 2 个（1 个扇区不叫转盘），至多 MaxSectors 个。
        var sectorLimit = Math.Clamp(maxSectors, 2, MaxSectors);

        // 除中选者之外的其他人，用来填满剩下的扇区。
        var others = names
            .Where(name => name is not null && !string.Equals(name, winner, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 名单里至少得有两个人才谈得上「转」。
        // 喵~防御：只有一个人（或去重后只剩中选者）时，扇区不是 1 个就是 0 个，转盘无意义。
        if (others.Count == 0)
        {
            return null;
        }

        // 给中选者留一个位子，剩下的位子从别人里抽，且不重复。
        var takeCount = Math.Min(sectorLimit - 1, others.Count);
        var pool = new List<string>(others);
        var sectors = new List<string>();
        while (sectors.Count < takeCount && pool.Count > 0)
        {
            // 抽一个下标，取出那个人，再从池子里删掉——这样保证不会重复。
            var index = Pick(pickIndexSelector, pool.Count);
            sectors.Add(pool[index]);
            // 池子最多 12 个人，这里的 O(n²) 完全不构成问题。
            pool.RemoveAt(index);
        }

        // 把中选者也放上去。
        sectors.Add(winner);

        // 洗牌，免得中选者每次都待在最后一个扇区上。
        for (var i = sectors.Count - 1; i > 0; i--)
        {
            // 在 [0, i] 里挑一个位置和当前位置交换。
            var j = Pick(pickIndexSelector, i + 1);
            // 交换两项。
            (sectors[i], sectors[j]) = (sectors[j], sectors[i]);
        }

        var sectorCount = sectors.Count;
        // 中选者在盘面上的位置。刚才明明放进去过，这里一定找得到。
        var winnerSector = sectors.IndexOf(winner);
        // 每个扇区占的角度。
        var sweep = 360.0 / sectorCount;

        // 硬币：决定停在「中选者左边那条缝」还是「右边那条缝」上。
        var stopOnLeftBoundary = Pick(pickIndexSelector, 2) == 0;
        var boundaryIndex = stopOnLeftBoundary ? winnerSector : (winnerSector + 1) % sectorCount;
        // 停在左边那条缝上就往右滑回去（-1），停在右边那条缝上就往左滑过来（+1）。
        var slideDirection = boundaryIndex == winnerSector ? -1 : 1;

        // 第一阶段：空转若干圈之后停在缝上。
        var boundaryAngle = AngleAtBoundary(boundaryIndex, sectorCount);
        // 第二阶段：往一侧滑半格，正好滑进中选者的扇区。
        var finalAngle = AngleAfterSlide(boundaryIndex, sectorCount, slideDirection);

        // 喵~防御：停到边界后必须滑得进中选者那一格，滑不进去说明公式被改坏了。
        // 这里做最后一道自检——宁可不出动画，也不能指着一个不是中选者的扇区停下来。
        if (SectorUnderPointer(finalAngle, sectorCount) != winnerSector)
        {
            return null;
        }

        // 喵~防御：时长不是正数时动画会一闪而过，兜底成 1 秒。
        var safeHold = holdDuration > TimeSpan.Zero ? holdDuration : TimeSpan.FromSeconds(1);

        return new WheelPlan(sectors, winnerSector, boundaryIndex, slideDirection,
            boundaryAngle, finalAngle, safeHold, SlideDuration, safeHold + SlideDuration, winner);
    }

    /// <summary>第 <paramref name="index"/> 个扇区的起始角度（度，0 是正上方，顺时针）。</summary>
    public static double SectorStart(int index, int count) =>
        // 每个扇区平分一圈。
        index * (360.0 / count);

    /// <summary>第 <paramref name="boundaryIndex"/> 条边界在轮盘本地坐标里的角度（度）。</summary>
    /// <remarks>第 b 条边界是扇区 b-1 和扇区 b 之间的那条缝。</remarks>
    public static double BoundaryAngle(int boundaryIndex, int count) =>
        // 边界角就等于它后面那个扇区的起始角。
        SectorStart(boundaryIndex, count);

    /// <summary>指针停在第 <paramref name="boundaryIndex"/> 条边界上时，轮盘应有的旋转角（度）。</summary>
    public static double AngleAtBoundary(int boundaryIndex, int count, int turns = Turns) =>
        // 先倒着空转 turns 圈，再转到那条边界压在指针底下。
        -(360.0 * turns) - BoundaryAngle(boundaryIndex, count);

    /// <summary>从边界滑进相邻扇区之后的最终旋转角（度）。</summary>
    /// <param name="direction">-1 表示向后（角度减小）、+1 表示向前（角度增大）。</param>
    public static double AngleAfterSlide(int boundaryIndex, int count, int direction, int turns = Turns) =>
        // 从边界出发挪半格——半格刚好跨过这条缝，落进相邻扇区的中心。
        AngleAtBoundary(boundaryIndex, count, turns) + direction * (360.0 / count) / 2.0;

    /// <summary>旋转到 <paramref name="angle"/> 度时，指针（固定在正上方）指着第几个扇区。</summary>
    public static int SectorUnderPointer(double angle, int count)
    {
        // 喵~防御：扇区数为 0 时下面会除零，直接给 0。
        if (count <= 0 || !double.IsFinite(angle))
        {
            return 0;
        }

        // 把角度归一化到 (-360, 0]。C# 的取余对负数保留符号，所以再手动挪一圈。
        var normalized = angle % 360.0;
        if (normalized > 0)
        {
            normalized -= 360.0;
        }

        // 换成「从正上方顺时针量」的 0~360 角度，判定条件就变成
        // i·sweep <= u < (i+1)·sweep，直接取整就是扇区号。
        var measured = -normalized;
        var sweep = 360.0 / count;
        var index = (int)Math.Floor(measured / sweep);

        // 喵~防御：浮点误差可能让 measured / sweep 算成 count（正好一整圈时），
        // 夹回最后一个扇区，避免越界。
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>从 0 到 n-1 里挑一个下标：生产环境用熵源，测试注入固定函数。</summary>
    private static int Pick(Func<int, int>? pickIndexSelector, int maxExclusive)
    {
        // 喵~防御：候选为空时给出 0，免得让 GetInt32(0) 抛异常。
        if (maxExclusive <= 0)
        {
            return 0;
        }

        var index = pickIndexSelector?.Invoke(maxExclusive) ?? RandomNumberGenerator.GetInt32(maxExclusive);
        // 喵~防御：注入的选择器可能给出越界下标，夹回合法范围。
        return Math.Clamp(index, 0, maxExclusive - 1);
    }
}
