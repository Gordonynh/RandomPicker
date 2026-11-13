using ClassIsland.Core.Extensions.Registry;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.RandomPicker.Automation;

/// <summary>
/// 抽选类「规则」。规则是规则集里的一个条件：触发器发生时，宿主逐条问这些规则是否为真，
/// 全部满足才执行行动组。插件注册规则之后，用户就能在自动化里搭自己的规则集了。
/// </summary>
/// <remarks>
/// <c>onHandle</c> 是静态委托，注册发生在 DI 容器还没建好的 <c>Initialize</c> 阶段，
/// 所以这里不构造函数注入，而是每次求值时从 <see cref="IAppHost.Host"/> 取单例——
/// 和插件设置页取 <c>PickerHostService</c> 是同一套路子。
/// </remarks>
public static class PickerRules
{
    public const string LastNameId = "gordon.randompicker.rule.lastName";
    public const string LastSourceId = "gordon.randompicker.rule.lastSource";
    public const string RemainingId = "gordon.randompicker.rule.remaining";

    /// <summary>最近抽中的人是（可填多个名字）。</summary>
    public static bool LastName(object? settings)
    {
        var s = settings as PickerRuleSettings;
        var last = CurrentEvents()?.LastPick;
        if (s is null || last is null)
        {
            return false;
        }

        return PickerMatch.AnyName(last.Names, s.Name, s.MatchMode, 1);
    }

    /// <summary>最近一次抽选方式是（任意 / 名单 / 文字名单 / 拍照 / 多人）。</summary>
    public static bool LastSource(object? settings)
    {
        var s = settings as PickerRuleSettings;
        var last = CurrentEvents()?.LastPick;
        if (s is null || last is null)
        {
            return false;
        }

        return PickerMatch.MatchesSource(last, s.Source);
    }

    /// <summary>本轮剩余人数不超过设定值。设成 0 表示不限制。</summary>
    public static bool Remaining(object? settings)
    {
        var s = settings as PickerRuleSettings;
        if (s is null || s.MaxRemaining <= 0)
        {
            return false;
        }

        var host = CurrentHost();
        if (host is null)
        {
            return false;
        }

        // 「不重复」模式才有轮次概念；其它模式返回名单总人数。
        return host.RemainingInRound <= s.MaxRemaining;
    }

    /// <summary>把这三个规则注册进宿主的规则表。</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddRule<PickerRuleSettings, PickerRuleSettingsControl>(
            LastNameId, "最近抽中的人是", "\uE8A5", LastName);
        services.AddRule<PickerRuleSettings, PickerRuleSettingsControl>(
            LastSourceId, "最近一次抽选方式是", "\uE8A5", LastSource);
        services.AddRule<PickerRuleSettings, PickerRuleSettingsControl>(
            RemainingId, "本轮剩余人数不超过", "\uE8A5", Remaining);
    }

    private static PickerEventService? CurrentEvents() =>
        IAppHost.Host?.Services.GetService<PickerEventService>();

    private static PickerHostService? CurrentHost() =>
        IAppHost.Host?.Services.GetService<PickerHostService>();
}
