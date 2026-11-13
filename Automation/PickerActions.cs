using System;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Automation;

/// <summary>把行动设置翻译成一次抽选请求。</summary>
internal static class PickerActionBuilder
{
    /// <param name="forced">行动自己强制的抽选方式；Auto 表示读设置里的 Kind。</param>
    public static PickRequest Build(PickerActionSettings? settings, PickKind forced)
    {
        var s = settings ?? new PickerActionSettings();
        var kind = forced != PickKind.Auto ? forced : PickerMatch.ParseKind(s.Kind);

        return new PickRequest
        {
            Kind = kind,
            Count = Math.Clamp(s.Count <= 0 ? 1 : s.Count, 1, 50),
            Roster = PickerMatch.ParseRoster(s.Roster),
            ShowCenterReveal = s.ShowCenterReveal,
            ShowNotification = s.ShowNotification,
            // 行动里明确选了「拍照抽人」就该真的拍照，不要被混入文字的概率改掉。
            HonorPhotoTextChance = false,
            Origin = "automation"
        };
    }

    public static void Run(PickerEventService events, PickerActionSettings? settings, PickKind forced) =>
        events.Request(Build(settings, forced));
}

/// <summary>
/// 四个抽选类行动共用一个设置类型和一个设置控件，只是各自强制一种抽选方式，
/// 这样「添加行动 → 随机抽选」里能直接挑到想要的那个。
/// </summary>
/// <remarks>
/// <b>每个行动必须直接继承 <see cref="ActionBase{TSettings}"/>。</b>
/// 宿主 <c>ActionBase.GetInstance</c> 只看 <c>GetType().BaseType.GetGenericArguments()</c>
/// 来推断设置类型；中间再插一层非泛型基类会让它拿不到 TSettings，
/// 结果 <c>SettingsInternal</c> 不被赋值，运行期间 <c>Settings</c> 一直是 null。
/// </remarks>

/// <summary>按插件当前模式抽一次（人数可调）。</summary>
[ActionInfo("gordon.randompicker.action.pick", "抽选一次", "\uE8A5",
    defaultGroupToMenu: "随机抽选")]
public sealed class PickOnceAction(PickerEventService events) : ActionBase<PickerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PickerActionBuilder.Run(events, Settings, PickKind.Auto);
    }
}

/// <summary>名单抽人。</summary>
[ActionInfo("gordon.randompicker.action.roster", "名单抽人", "\uE8A5",
    defaultGroupToMenu: "随机抽选")]
public sealed class RosterPickAction(PickerEventService events) : ActionBase<PickerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PickerActionBuilder.Run(events, Settings, PickKind.Roster);
    }
}

/// <summary>拍照抽人。</summary>
[ActionInfo("gordon.randompicker.action.photo", "拍照抽人", "\uE722",
    defaultGroupToMenu: "随机抽选")]
public sealed class PhotoPickAction(PickerEventService events) : ActionBase<PickerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PickerActionBuilder.Run(events, Settings, PickKind.Photo);
    }
}

/// <summary>一次抽多人。没填人数时默认 3 人。</summary>
[ActionInfo("gordon.randompicker.action.multi", "一次抽多人", "\uE902",
    defaultGroupToMenu: "随机抽选")]
public sealed class MultiPickAction(PickerEventService events) : ActionBase<PickerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        var settings = Settings ?? new PickerActionSettings();
        PickerActionBuilder.Run(events,
            new PickerActionSettings
            {
                Kind = settings.Kind,
                Count = settings.Count > 1 ? settings.Count : 3,
                Roster = settings.Roster,
                ShowCenterReveal = settings.ShowCenterReveal,
                ShowNotification = settings.ShowNotification
            }, PickKind.Roster);
    }
}

/// <summary>抽一个小组。组员人数在插件设置里配。</summary>
[ActionInfo("gordon.randompicker.action.group", "抽一个小组", "\uE902",
    defaultGroupToMenu: "随机抽选")]
public sealed class GroupPickAction(PickerEventService events) : ActionBase<PickerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PickerActionBuilder.Run(events, Settings, PickKind.Group);
    }
}

/// <summary>隐藏悬浮窗。</summary>
[ActionInfo("gordon.randompicker.action.hideWindow", "隐藏悬浮窗", "\uE8A5",
    defaultGroupToMenu: "随机抽选")]
public sealed class HideFloatingWindowAction(PickerHostService host) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        host.SetFloatingWindowVisible(false);
    }
}

/// <summary>显示悬浮窗。</summary>
[ActionInfo("gordon.randompicker.action.showWindow", "显示悬浮窗", "\uE8A5",
    defaultGroupToMenu: "随机抽选")]
public sealed class ShowFloatingWindowAction(PickerHostService host) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        host.SetFloatingWindowVisible(true);
    }
}
