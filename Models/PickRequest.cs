using System;

namespace ClassIsland.RandomPicker.Models;

/// <summary>这一抽是从哪儿来的。</summary>
public enum PickSource
{
    /// <summary>主名单（名单.txt）。</summary>
    Roster,

    /// <summary>拍照模式下混入文字、或单独指定的「名单-文字.txt」。</summary>
    Text,

    /// <summary>摄像头拍照，抽的是画面里的人脸。</summary>
    Photo,

    /// <summary>按小组抽，抽中的是一组人。</summary>
    Group
}

/// <summary>这一抽走哪条路。</summary>
public enum PickKind
{
    /// <summary>跟随插件当前模式（悬浮窗点击走这条）。</summary>
    Auto,

    /// <summary>强制按名单抽。</summary>
    Roster,

    /// <summary>强制拍照抽人。</summary>
    Photo,

    /// <summary>强制抽小组。</summary>
    Group
}

/// <summary>文字抽选用哪份名单。</summary>
public enum PickRosterSource
{
    /// <summary>跟随插件设置（拍照模式可能用单独名单）。</summary>
    Default,

    /// <summary>强制用主名单。</summary>
    Main,

    /// <summary>强制用文字名单。</summary>
    Text
}

/// <summary>
/// 一次抽选请求。悬浮窗点击、URL、本地 HTTP、自动化动作都归一到这一个对象上。
/// </summary>
/// <remarks>
/// 可空字段表示「跟随插件设置」，不覆盖用户的日常选择；外部调用只写自己关心的项。
/// </remarks>
public sealed class PickRequest
{
    /// <summary>抽选方式。Auto = 用当前设置里的模式。</summary>
    public PickKind Kind { get; init; } = PickKind.Auto;

    /// <summary>只覆盖「名单抽选」时的模式（Random / NoRepeat）；null = 跟随设置。</summary>
    public PickMode? Mode { get; init; }

    /// <summary>一次抽几个人。小于 1 时按 1 处理；拍照模式下表示一次裁几个人像。</summary>
    public int Count { get; init; } = 1;

    /// <summary>文字抽选用哪份名单。</summary>
    public PickRosterSource Roster { get; init; } = PickRosterSource.Default;

    /// <summary>是否弹中央大字。null = 跟随设置。</summary>
    public bool? ShowCenterReveal { get; init; }

    /// <summary>是否发 ClassIsland 提醒。null = 跟随设置。</summary>
    public bool? ShowNotification { get; init; }

    /// <summary>
    /// 拍照模式下是否按「混入文字抽选」的概率改抽名字。
    /// 悬浮窗点击为 true；外部明确指定拍照时关掉，避免调用方要拍照却拿到文字。
    /// </summary>
    public bool HonorPhotoTextChance { get; init; } = true;

    /// <summary>调用来源，仅用于事件与日志：window / uri / http / automation。</summary>
    public string Origin { get; init; } = "window";

    /// <summary>悬浮窗点击用的默认请求。</summary>
    public static PickRequest FromWindow() => new() { Origin = "window" };

    /// <summary>把人数规范到 1~名单上限之间。</summary>
    public int NormalizedCount(int maxCount) =>
        Math.Clamp(Count <= 0 ? 1 : Count, 1, Math.Max(1, maxCount));
}
