using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 抽组：从「A 组 / B 组 / …」或者「1 组 / 2 组 / …」里随机挑出若干个。
/// </summary>
/// <remarks>
/// 组不是名单——它由 <see cref="PickerSettings.GroupCount"/> 和
/// <see cref="PickerSettings.GroupStyle"/> 直接算出来，不读文件，所以改了设置立刻生效。
/// <para/>
/// 抽选逻辑跟 <see cref="RosterService.PickMany"/> 是一套：同一批内不重复，
/// 不放回时跨批次也不重复，抽完一轮自动开新一轮。
/// </remarks>
public static class GroupPicker
{
    /// <summary>
    /// 按当前设置列出所有组的标签，顺序就是 A、B、C…… 或 1、2、3……
    /// </summary>
    public static List<string> Labels(PickerSettings settings)
    {
        var count = Math.Clamp(settings.GroupCount, 1, PickerSettings.MaxGroups);
        var labels = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            labels.Add(settings.GroupStyle == GroupStyle.Letter
                ? $"{(char)('A' + i)} 组"
                : $"{i + 1} 组");
        }

        return labels;
    }

    /// <summary>
    /// 一次抽 <paramref name="count"/> 个组，<b>这一批里互不重复</b>。
    /// </summary>
    /// <remarks>
    /// 口径跟抽人一样：不放回时本轮抽过的组不再出现，放回时按「最近抽过」降权。
    /// 随机数走 <see cref="RandomNumberGenerator"/>：每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// </remarks>
    public static List<string> PickMany(PickerSettings settings, int count)
    {
        var picked = new List<string>();
        var labels = Labels(settings);
        if (labels.Count == 0 || count <= 0)
        {
            return picked;
        }

        // 一次抽不了比组数还多的组。
        count = Math.Min(count, labels.Count);

        if (settings.IsNoRepeat)
        {
            PickWithoutReplacement(settings, count, labels, picked);
        }
        else
        {
            PickWithReplacement(settings, count, labels, picked);
        }

        if (picked.Count > 0)
        {
            settings.LastGroupPicked = picked[^1];
            foreach (var group in picked)
            {
                WeightedPicker.Remember(settings.RecentGroups, group);
            }
        }

        return picked;
    }

    /// <summary>不放回：本轮没抽过的组里等概率挑。</summary>
    private static void PickWithoutReplacement(PickerSettings settings, int count,
        List<string> labels, List<string> picked)
    {
        var remaining = labels
            .Where(x => !settings.DrawnGroupsThisRound.Contains(x, StringComparer.Ordinal))
            .ToList();

        for (var i = 0; i < count; i++)
        {
            if (remaining.Count == 0)
            {
                // 一轮抽完了：开新一轮。这一批已经抽到的仍然排除在外。
                settings.DrawnGroupsThisRound.Clear();
                remaining = labels.Where(x => !picked.Contains(x)).ToList();

                // 上一轮最后一组和这一轮第一组是挨着的，撞上就像抽重了。
                if (remaining.Count > 1 && settings.LastGroupPicked is { } last)
                {
                    remaining.RemoveAll(x => string.Equals(x, last, StringComparison.Ordinal));
                }

                if (remaining.Count == 0)
                {
                    break;
                }
            }

            var chosen = remaining[RandomNumberGenerator.GetInt32(remaining.Count)];
            picked.Add(chosen);
            remaining.Remove(chosen);
            settings.DrawnGroupsThisRound.Add(chosen);
        }
    }

    /// <summary>放回：每次都从全体里挑，按「最近抽过」降权，同一批里不重复。</summary>
    private static void PickWithReplacement(PickerSettings settings, int count,
        List<string> labels, List<string> picked)
    {
        for (var i = 0; i < count; i++)
        {
            var pool = labels.Where(x => !picked.Contains(x)).ToList();
            if (pool.Count == 0)
            {
                break;
            }

            picked.Add(WeightedPicker.Pick(pool, settings.RecentGroups));
        }
    }

    /// <summary>本轮还剩几组没抽到。放回模式下没有轮次概念，等于总组数。</summary>
    public static int RemainingInRound(PickerSettings settings)
    {
        var labels = Labels(settings);
        if (!settings.IsNoRepeat)
        {
            return labels.Count;
        }

        return Math.Max(0, labels.Count - labels.Count(x =>
            settings.DrawnGroupsThisRound.Contains(x, StringComparer.Ordinal)));
    }

    /// <summary>手动开始新一轮。</summary>
    /// <remarks>
    /// 改了组数或组名写法之后也应该调一次——旧的「E 组」记录留着没有意义。
    /// <para/>
    /// 只清「本轮抽过哪些」，<b>不清「上一组是谁」</b>：新一轮的第一抽也要避开刚抽过的那一组，
    /// 否则连着两次抽到同一组，看起来就像抽重了。跟名单那边的口径保持一致。
    /// </remarks>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnGroupsThisRound.Clear();
    }
}
