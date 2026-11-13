using System;
using System.Collections.Generic;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 把 URL 的路径 + 查询串翻译成 <see cref="PickRequest"/>。
/// </summary>
/// <remarks>
/// 两套入口（<c>classisland://plugins/randompicker/...</c> 和本地 HTTP）共用这一个解析器，
/// 保证「同一个 URL 参数，走哪条路结果一样」。
/// <para/>
/// 路径末段决定抽选方式：<c>photo</c> 拍照、<c>roster</c> 名单、<c>group</c> 抽小组、
/// <c>multi</c> 一次抽多人，其余（含 <c>pick</c> 和空）跟随插件当前模式。
/// 查询参数可以覆盖：
/// <c>kind</c>=auto/roster/photo/group、<c>mode</c>=photo/random/norepeat/group/roster、
/// <c>count</c>=人数、<c>roster</c>=default/main/text、<c>reveal</c> / <c>notify</c>=0/1。
/// </remarks>
public static class PickRequestParser
{
    public static PickRequest Parse(string path, string query, PickerSettings settings, string origin)
    {
        var q = ParseQuery(query);
        var segment = LastSegment(path);

        var kind = KindFromSegment(segment, settings);
        PickMode? modeOverride = null;

        if (q.TryGetValue("kind", out var kindValue))
        {
            kind = kindValue.ToLowerInvariant() switch
            {
                "photo" => PickKind.Photo,
                "roster" => PickKind.Roster,
                "group" => PickKind.Group,
                _ => PickKind.Auto
            };
        }

        if (q.TryGetValue("mode", out var modeValue))
        {
            switch (modeValue.ToLowerInvariant())
            {
                case "photo":
                    kind = PickKind.Photo;
                    break;
                case "group":
                    kind = PickKind.Group;
                    break;
                case "random":
                    kind = PickKind.Roster;
                    modeOverride = PickMode.Random;
                    break;
                case "norepeat":
                case "no-repeat":
                    kind = PickKind.Roster;
                    modeOverride = PickMode.NoRepeat;
                    break;
                case "roster":
                case "text":
                    kind = PickKind.Roster;
                    break;
            }
        }

        var count = 1;
        if (segment == "multi")
        {
            count = Math.Max(1, settings.DefaultMultiCount);
        }

        if (q.TryGetValue("count", out var countValue) &&
            int.TryParse(countValue, out var parsed) && parsed > 0)
        {
            count = Math.Clamp(parsed, 1, 50);
        }

        var roster = PickRosterSource.Default;
        if (q.TryGetValue("roster", out var rosterValue))
        {
            roster = rosterValue.ToLowerInvariant() switch
            {
                "main" => PickRosterSource.Main,
                "text" => PickRosterSource.Text,
                _ => PickRosterSource.Default
            };
        }

        return new PickRequest
        {
            Kind = kind,
            Mode = modeOverride,
            Count = count,
            Roster = roster,
            ShowCenterReveal = BoolQuery(q, "reveal"),
            ShowNotification = BoolQuery(q, "notify"),
            // URL 明确写了要什么，就别再被「混入文字抽选」的概率改掉。
            HonorPhotoTextChance = false,
            Origin = origin
        };
    }

    /// <summary>解析查询串。键不区分大小写，值做 URL 解码。</summary>
    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? string.Empty : part[(eq + 1)..];
            result[Uri.UnescapeDataString(key)] =
                Uri.UnescapeDataString(value.Replace("+", " "));
        }

        return result;
    }

    private static PickKind KindFromSegment(string segment, PickerSettings settings) => segment switch
    {
        "photo" => PickKind.Photo,
        "roster" => PickKind.Roster,
        "group" => PickKind.Group,
        "multi" => PickKind.Roster,
        // 当前模式是「小组抽选」时，不带子路径的 URL 也应该抽组。
        _ => settings.Mode == PickMode.Group ? PickKind.Group : PickKind.Auto
    };

    private static string LastSegment(string path)
    {
        var clean = (path ?? string.Empty).Split('?')[0].Trim('/');
        var segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? string.Empty
            : segments[^1].ToLowerInvariant();
    }

    private static bool? BoolQuery(IReadOnlyDictionary<string, string> query, string key)
    {
        if (!query.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }
}
