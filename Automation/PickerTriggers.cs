using System;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Automation;

/// <summary>
/// 抽选类触发器的共同骨架：加载时订阅 <see cref="PickerEventService.Picked"/>，
/// 卸载时一定要退订——用户可以反复开关同一条自动化，漏了 <c>-=</c> 就会重复触发并泄漏。
/// </summary>
public abstract class PickerTriggerBase : TriggerBase
{
    private readonly PickerEventService _events;

    protected PickerTriggerBase(PickerEventService events) => _events = events;

    public override void Loaded() => _events.Picked += OnPicked;

    public override void UnLoaded() => _events.Picked -= OnPicked;

    private void OnPicked(object? sender, PickEventArgs args)
    {
        if (ShouldFire(args))
        {
            Trigger();
        }
    }

    /// <summary>这一抽要不要触发。</summary>
    protected abstract bool ShouldFire(PickEventArgs args);
}

/// <summary>抽中人选时（任意一次抽选完成）。</summary>
[TriggerInfo("gordon.randompicker.trigger.picked", "抽中人选时", "\uE8A5")]
public sealed class AnyPickedTrigger(PickerEventService events) : PickerTriggerBase(events)
{
    protected override bool ShouldFire(PickEventArgs args) => true;
}

/// <summary>名单抽人时（从名单里抽到名字）。</summary>
[TriggerInfo("gordon.randompicker.trigger.roster", "名单抽人时", "\uE8A5")]
public sealed class RosterPickedTrigger(PickerEventService events) : PickerTriggerBase(events)
{
    protected override bool ShouldFire(PickEventArgs args) =>
        args.Source is PickSource.Roster or PickSource.Text;
}

/// <summary>拍照抽人时。</summary>
[TriggerInfo("gordon.randompicker.trigger.photo", "拍照抽人时", "\uE722")]
public sealed class PhotoPickedTrigger(PickerEventService events) : PickerTriggerBase(events)
{
    protected override bool ShouldFire(PickEventArgs args) => args.Source == PickSource.Photo;
}

/// <summary>一次抽多人时。</summary>
[TriggerInfo("gordon.randompicker.trigger.multi", "一次抽多人时", "\uE902")]
public sealed class MultiPickedTrigger(PickerEventService events) : PickerTriggerBase(events)
{
    protected override bool ShouldFire(PickEventArgs args) => args.IsMulti;
}

/// <summary>
/// 当抽中指定名字时。可以在触发器的设置界面里填名字（支持多个、支持包含匹配）。
/// </summary>
/// <remarks>
/// 单独继承 <see cref="TriggerBase{T}"/> 而不是 <see cref="PickerTriggerBase"/>，
/// 因为只有带设置控件的触发器才会被宿主注入 <c>SettingsInternal</c>。
/// </remarks>
[TriggerInfo("gordon.randompicker.trigger.pickedName", "当抽中指定名字时", "\uE8A5")]
public sealed class NamePickedTrigger(PickerEventService events) : TriggerBase<PickerNameMatchSettings>
{
    public override void Loaded() => events.Picked += OnPicked;

    public override void UnLoaded() => events.Picked -= OnPicked;

    private void OnPicked(object? sender, PickEventArgs args)
    {
        var settings = Settings;
        if (settings is null)
        {
            return;
        }

        if (PickerMatch.AnyName(args.Names, settings.Name, settings.MatchMode, settings.MinCount))
        {
            Trigger();
        }
    }
}

/// <summary>当悬浮窗被隐藏时。</summary>
[TriggerInfo("gordon.randompicker.trigger.windowHidden", "当悬浮窗隐藏时", "\uE8A5")]
public sealed class FloatingWindowHiddenTrigger(PickerEventService events) : TriggerBase
{
    public override void Loaded() => events.FloatingWindowVisibilityChanged += OnChanged;

    public override void UnLoaded() => events.FloatingWindowVisibilityChanged -= OnChanged;

    private void OnChanged(object? sender, FloatingWindowVisibilityEventArgs e)
    {
        if (!e.IsVisible)
        {
            Trigger();
        }
    }
}

/// <summary>当悬浮窗被显示时。</summary>
[TriggerInfo("gordon.randompicker.trigger.windowShown", "当悬浮窗显示时", "\uE8A5")]
public sealed class FloatingWindowShownTrigger(PickerEventService events) : TriggerBase
{
    public override void Loaded() => events.FloatingWindowVisibilityChanged += OnChanged;

    public override void UnLoaded() => events.FloatingWindowVisibilityChanged -= OnChanged;

    private void OnChanged(object? sender, FloatingWindowVisibilityEventArgs e)
    {
        if (e.IsVisible)
        {
            Trigger();
        }
    }
}
