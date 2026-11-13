using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Automation;

/// <summary>名字匹配、抽选方式解析这类共用逻辑。</summary>
internal static class PickerMatch
{
    private static readonly char[] Separators = [',', '，', '、', ';', '；', '\n', '\r', '\t'];

    /// <summary>
    /// 抽中的名字里是否有至少一个符合配置。多个名字用逗号、顿号或换行分隔，任一命中即可。
    /// </summary>
    /// <param name="minHits">至少命中几个名字才成立。</param>
    public static bool AnyName(IReadOnlyList<string> names, string? pattern,
        string? matchMode, int minHits)
    {
        if (names.Count == 0 || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var parts = pattern.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            return false;
        }

        var contains = string.Equals(matchMode, "contains", StringComparison.OrdinalIgnoreCase);
        var hits = names.Count(name => parts.Any(part =>
            contains
                ? name.Contains(part, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, part, StringComparison.OrdinalIgnoreCase)));

        return hits >= Math.Max(1, minHits);
    }

    /// <summary>判断一次抽选是否属于给定的来源描述。</summary>
    public static bool MatchesSource(PickEventArgs args, string? source) => source switch
    {
        "roster" => args.Source == PickSource.Roster,
        "text" => args.Source == PickSource.Text,
        "photo" => args.Source == PickSource.Photo,
        "group" => args.Source == PickSource.Group,
        "multi" => args.IsMulti,
        "single" => !args.IsMulti,
        _ => true
    };

    public static PickKind ParseKind(string? kind) => kind switch
    {
        "roster" => PickKind.Roster,
        "photo" => PickKind.Photo,
        "group" => PickKind.Group,
        _ => PickKind.Auto
    };

    public static PickRosterSource ParseRoster(string? roster) => roster switch
    {
        "main" => PickRosterSource.Main,
        "text" => PickRosterSource.Text,
        _ => PickRosterSource.Default
    };
}
