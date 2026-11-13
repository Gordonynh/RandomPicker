using System;
using System.Collections.Generic;

namespace ClassIsland.RandomPicker.Models;

/// <summary>
/// 一次抽选完成后的结果。自动化触发器订阅它，规则读它，URL 接口也可以拿它回 JSON。
/// </summary>
/// <remarks>
/// 拍照抽人没有名字，<see cref="Names"/> 是空的，靠 <see cref="Source"/> 和
/// <see cref="DisplayText"/> 描述；名单抽人则是抽到的名字列表。
/// </remarks>
public sealed class PickEventArgs : EventArgs
{
    /// <summary>抽中的名字。拍照抽人时为空。</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>给用户看的一句话：名单抽人是「张三、李四」，拍照抽人是「从 32 人中抽取 2 人」。</summary>
    public string DisplayText { get; init; } = string.Empty;

    /// <summary>抽选来源。</summary>
    public PickSource Source { get; init; } = PickSource.Roster;

    /// <summary>抽选时生效的模式。</summary>
    public PickMode Mode { get; init; }

    /// <summary>这一抽一共抽了几个人（拍照时是裁出的人像数）。</summary>
    public int Count { get; init; } = 1;

    /// <summary>附带说明，比如从拍照失败退回名单的原因。</summary>
    public string? Note { get; init; }

    /// <summary>调用来源：window / uri / http / automation。</summary>
    public string Origin { get; init; } = "window";

    /// <summary>抽选发生的时间。</summary>
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    /// <summary>一次抽了多个人吗。</summary>
    public bool IsMulti => Count > 1;
}
