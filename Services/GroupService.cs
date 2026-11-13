using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>一组人。<see cref="Index"/> 从 1 开始，和设置里保存的轮次记录对应。</summary>
public sealed record PickGroup(int Index, string Label, IReadOnlyList<string> Members);

/// <summary>
/// 小组抽选：把当前名单按顺序切成若干组，从本轮没抽过的组里等概率抽一组。
/// </summary>
/// <remarks>
/// 分组只按名单顺序切，不读额外文件——换一份名单就自动换一套组，不用再维护一份组配置。
/// 最后一组人数不足时照样成组，不会出现「剩下的几个人没组可去」。
/// <para/>
/// 组没有权重概念，等概率即可；随机数沿用 <see cref="RandomNumberGenerator"/>，
/// 和按人抽选是同一个熵源。
/// </remarks>
public static class GroupService
{
    public const int MinGroupSize = 1;
    public const int MaxGroupSize = 50;

    /// <summary>按每组人数切分名单。</summary>
    public static List<PickGroup> Build(IReadOnlyList<string> names, int groupSize)
    {
        var result = new List<PickGroup>();
        if (names.Count == 0)
        {
            return result;
        }

        var size = Math.Clamp(groupSize, MinGroupSize, MaxGroupSize);
        for (var start = 0; start < names.Count; start += size)
        {
            var members = names.Skip(start).Take(size).ToArray();
            result.Add(new PickGroup(result.Count + 1, $"第 {result.Count + 1} 组", members));
        }

        return result;
    }

    /// <summary>不真的切分，只算一共能分成几组。悬浮窗上显示用。</summary>
    public static int GroupCount(int nameCount, int groupSize)
    {
        if (nameCount <= 0)
        {
            return 0;
        }

        var size = Math.Clamp(groupSize, MinGroupSize, MaxGroupSize);
        return (nameCount + size - 1) / size;
    }

    /// <summary>
    /// 抽一组。本轮抽过的组不再出现，抽完自动开新一轮，
    /// 新一轮的第一抽会避开上一轮最后一组，免得看起来像连着抽到同一组。
    /// </summary>
    public static PickGroup Pick(List<PickGroup> groups, PickerSettings settings)
    {
        var drawn = new HashSet<int>(settings.DrawnGroupsThisRound ?? new List<int>());
        var pool = groups.Where(x => !drawn.Contains(x.Index)).ToList();

        if (pool.Count == 0)
        {
            drawn.Clear();
            pool = [.. groups];
            if (pool.Count > 1 && settings.LastGroupIndex is { } last)
            {
                pool.RemoveAll(x => x.Index == last);
            }

            if (pool.Count == 0)
            {
                pool = [.. groups];
            }
        }

        var picked = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        drawn.Add(picked.Index);
        settings.DrawnGroupsThisRound = drawn.ToList();
        settings.LastGroupIndex = picked.Index;
        return picked;
    }

    /// <summary>手动开始新一轮小组抽选。</summary>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnGroupsThisRound.Clear();
        settings.LastGroupIndex = null;
    }
}
