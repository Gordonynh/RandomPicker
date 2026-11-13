namespace ClassIsland.RandomPicker.Models;

/// <summary>
/// 「当抽中指定名字时」触发器 / 「最近抽中的人是」规则用的设置。
/// </summary>
/// <remarks>
/// 属性一律用 string / int / bool，不用枚举：ClassIsland 把行动、规则的设置按
/// <c>JsonElement</c> 存进自动化配置，反序列化时枚举容易因为写成字符串而失配。
/// </remarks>
public sealed class PickerNameMatchSettings
{
    /// <summary>要匹配的名字。多个用英文逗号、中文顿号或换行分隔，任一命中就算命中。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"exact" = 完全相等；"contains" = 名字里包含这段文字。</summary>
    public string MatchMode { get; set; } = "exact";

    /// <summary>命中人数至少几个才触发。默认 1。</summary>
    public int MinCount { get; set; } = 1;
}

/// <summary>
/// 抽选类「行动」的设置。四个行动共用这一份设置，只是各自强制一种抽选方式。
/// </summary>
public sealed class PickerActionSettings
{
    /// <summary>"auto" / "roster" / "photo"。由行动自己覆盖，仅 auto 时读这里。</summary>
    public string Kind { get; set; } = "auto";

    /// <summary>一次抽几个人。拍照模式下是裁几个人像。</summary>
    public int Count { get; set; } = 1;

    /// <summary>"default" / "main" / "text"。</summary>
    public string Roster { get; set; } = "default";

    /// <summary>是否显示屏幕中央大字。</summary>
    public bool ShowCenterReveal { get; set; } = true;

    /// <summary>是否发送 ClassIsland 提醒。</summary>
    public bool ShowNotification { get; set; } = true;
}

/// <summary>抽选类「规则」的设置。三个规则共用，各自只读自己关心的字段。</summary>
public sealed class PickerRuleSettings
{
    /// <summary>名字匹配用，同 <see cref="PickerNameMatchSettings.Name"/>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"exact" / "contains"。</summary>
    public string MatchMode { get; set; } = "exact";

    /// <summary>抽选方式："any" / "roster" / "text" / "photo" / "multi"。</summary>
    public string Source { get; set; } = "any";

    /// <summary>本轮剩余人数上限；0 表示不限制。</summary>
    public int MaxRemaining { get; set; }
}
