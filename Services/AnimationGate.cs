using System;
using System.Collections.Generic;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 动画时长：把配置里的秒数夹到合理的区间。
/// </summary>
/// <remarks>
/// <b>这里只管「播多久」，不管「播不播」。</b>
/// <para/>
/// 「播不播」曾经由这里的一个 <c>Resolve</c> 方法决定，它会读宿主的
/// <c>IThemeService.AnimationLevel</c>，在 ClassIsland 关掉动画时把插件一起掐安静。
/// 2026-09-15 主人决定把那条路拆掉：<b>动画是这个功能本身，不是界面装饰</b>，
/// ClassIsland 的「动画级别」管的是它自己的界面过渡，关掉它不该连累抽选表演。
/// 现在唯一能关掉动画的地方是插件右键菜单里的「无动画」。
/// 这条政策有两条测试钉着（见 <c>AnimationGateTests</c>），其中一条会在
/// 程序集元数据里扫 <c>IThemeService</c>，防止有人把它接回来。
/// </remarks>
internal static class AnimationGate
{
    /// <summary>
    /// 这段动画实际要播多久。
    /// </summary>
    /// <param name="style">动画样式。</param>
    /// <param name="settings">插件设置，提供各样式自己的时长。</param>
    /// <param name="slotCount">老虎机的格数；其他样式忽略这个参数。</param>
    public static TimeSpan DurationOf(RevealAnimationStyle style, PickerSettings settings, int slotCount = 0)
    {
        // 喵~防御：设置对象不该为 null，早报错好定位。
        ArgumentNullException.ThrowIfNull(settings);

        switch (style)
        {
            case RevealAnimationStyle.Scroll:
                // 滚动名字：0.5 秒到 20 秒之间。
                return TimeSpan.FromSeconds(ClampSeconds(settings.ScrollDurationSeconds, 0.5, 20));

            case RevealAnimationStyle.Csgo:
                // CSGO 开箱：同样 0.5 秒到 20 秒。
                return TimeSpan.FromSeconds(ClampSeconds(settings.CsgoDurationSeconds, 0.5, 20));

            case RevealAnimationStyle.Slot:
            {
                // 格数兜底成下限：调用方还没算出格数时，也得给一个像样的总时长。
                var slots = Math.Clamp(slotCount <= 0 ? SlotMachine.MinSlots : slotCount,
                    SlotMachine.MinSlots, SlotMachine.MaxSlots);
                // 单格 0.2 秒到 10 秒，这里是「旋转总时长」= 单格 × 格数。
                // 排片表还会在这个基础上再加一段定格（SlotMachine.SettleSeconds），
                // 让最后一格停住之后板面静止一会儿再出结果。
                return TimeSpan.FromSeconds(ClampSeconds(settings.SlotStepSeconds, 0.2, 10) * slots);
            }

            case RevealAnimationStyle.Wheel:
                // 转盘：转到边界用 1 到 20 秒，再加上固定不变的「滑进扇区」那一下。
                return TimeSpan.FromSeconds(ClampSeconds(settings.WheelDurationSeconds, 1.0, 20))
                       + WheelLayout.SlideDuration;

            default:
                // 无动画或未知样式：零时长，调用方会走「直接出结果」的分支。
                return TimeSpan.Zero;
        }
    }

    /// <summary>把配置里的秒数夹到合理区间，并挡掉 NaN / Infinity。</summary>
    private static double ClampSeconds(double seconds, double minimum, double maximum)
    {
        // 喵~防御：配置被手改成 NaN 或 Infinity 时，Math.Clamp 会原样把它们放过去，
        // 时长就成了 NaN，动画永远结束不了（连「停留 X 秒」都跟着失效）。
        // 这种情况下退回下限——宁可快放，也不能把界面卡住。
        if (!double.IsFinite(seconds))
        {
            return minimum;
        }

        return Math.Clamp(seconds, minimum, maximum);
    }
}

/// <summary>
/// 把「样式 + 名单 + 中选者」翻译成具体的排片表。
/// </summary>
/// <remarks>
/// 单独拆出来是为了让这段映射本身也能被单测覆盖：动画控件只负责把计划画出来，
/// 「该用哪张计划」这件事在这里决定。
/// </remarks>
internal static class RevealAnimationPlanner
{
    /// <summary>方块高度的倍率（相对中央大字的字号）。</summary>
    private const double SlotHeightRatio = 1.15;

    /// <summary>方块宽度的倍率。</summary>
    private const double SlotWidthRatio = 1.5;

    /// <summary>方块间距的倍率。</summary>
    private const double GapRatio = 0.12;

    /// <summary>CSGO 可视区宽度的倍率（大概能同时看到四块半）。</summary>
    private const double ViewportWidthRatio = 4.6;

    /// <summary>CSGO 可视区高度的倍率。</summary>
    private const double ViewportHeightRatio = 1.6;

    /// <summary>
    /// 生成排片表。
    /// </summary>
    /// <param name="style">要播的样式。</param>
    /// <param name="names">当前名单。</param>
    /// <param name="winner">中选者。</param>
    /// <param name="settings">插件设置。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到 n-1 里挑一个下标的函数，用来给 CSGO 的方块摇装饰用品质、以及挑转盘扇区。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <param name="stopOffsetSelector">
    /// 返回一个 0~1 的随机数，用来决定 CSGO 的指针停在方块内的哪个位置。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>排片表；数据不满足这个样式的要求时返回 <c>null</c>，由上层降级成不播动画。</returns>
    /// <remarks>
    /// <b>定格在这里统一加上去。</b>各样式自己的构建方法只负责算「动作演多久」，
    /// 这里再给每种样式都补上同一段 <see cref="RevealAnimationPlan.SettleSeconds"/>。
    /// 集中在一处是为了让四种样式的收尾节奏必然一致——
    /// 分散到四个地方写，早晚会有一个样式被漏掉（这个 bug 就真发生过两次）。
    /// <para/>
    /// 定格只是记在排片表上的时长，<b>不参与动画本身</b>：
    /// <see cref="RevealAnimationBase.PlayAsync"/> 播完动作之后原地等这么一会儿。
    /// </remarks>
    public static RevealAnimationPlan? Build(RevealAnimationStyle style,
        IReadOnlyList<string>? names, string? winner, PickerSettings settings,
        Func<int, int>? pickIndexSelector = null,
        Func<double>? stopOffsetSelector = null)
    {
        var plan = BuildMotion(style, names, winner, settings, pickIndexSelector, stopOffsetSelector);

        // 喵~防御：造不出排片表（无动画，或数据不满足这个样式）时原样返回 null。
        if (plan is null)
        {
            return null;
        }

        // 动作之后接一段定格：画面停住不动，好让人看清结果。
        return plan with { Total = plan.Total + TimeSpan.FromSeconds(RevealAnimationPlan.SettleSeconds) };
    }

    /// <summary>
    /// 生成「动作那一段」的排片表——不含收尾的定格。
    /// </summary>
    /// <param name="style">要播的样式。</param>
    /// <param name="names">当前名单。</param>
    /// <param name="winner">中选者。</param>
    /// <param name="settings">插件设置。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到 n-1 里挑一个下标的函数，用来给 CSGO 的方块摇装饰用品质、以及挑转盘扇区。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <param name="stopOffsetSelector">
    /// 返回一个 0~1 的随机数，用来决定 CSGO 的指针停在方块内的哪个位置。
    /// 传 <c>null</c> 用操作系统的熵源；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>排片表；数据不满足这个样式的要求时返回 <c>null</c>。</returns>
    private static RevealAnimationPlan? BuildMotion(RevealAnimationStyle style,
        IReadOnlyList<string>? names, string? winner, PickerSettings settings,
        Func<int, int>? pickIndexSelector, Func<double>? stopOffsetSelector)
    {
        // 喵~防御：设置对象不允许为 null。
        ArgumentNullException.ThrowIfNull(settings);

        // 名单为空或没有中选者时，任何样式都无从演起。
        if (names is null || names.Count == 0 || string.IsNullOrEmpty(winner))
        {
            return null;
        }

        switch (style)
        {
            case RevealAnimationStyle.Scroll:
            {
                // 滚动名字：只要名单里有中选者就能播。
                var duration = AnimationGate.DurationOf(style, settings);
                return ScrollTicks.BuildPlan(names, winner, duration.TotalSeconds);
            }

            case RevealAnimationStyle.Csgo:
            {
                // 方块尺寸跟着中央大字的字号档位走，保证和插件其它部分同一套视觉尺度。
                var slotHeight = settings.RevealFontSize * SlotHeightRatio;
                var slotWidth = settings.RevealFontSize * SlotWidthRatio;
                var gap = settings.RevealFontSize * GapRatio;
                var viewportWidth = settings.RevealFontSize * ViewportWidthRatio;
                var viewportHeight = slotHeight * ViewportHeightRatio;
                var duration = AnimationGate.DurationOf(style, settings);
                // 中选者的品质在这里摇一次。它只决定方块的颜色，
                // 和「铁面无私地抽到谁」毫无关系——两者是各自独立的随机。
                var winnerRarity = ItemRarityTable.Roll(settings.RarityWeights, pickIndexSelector);
                return CsgoTrack.Build(names, winner,
                    viewportWidth, viewportHeight, slotWidth, slotHeight, gap, duration,
                    winnerRarity, settings.RarityWeights, pickIndexSelector, stopOffsetSelector);
            }

            case RevealAnimationStyle.Slot:
            {
                // 老虎机对名字长度有硬要求：超过 4 个字就显示不完整。
                // 这种名单上回退到滚动名字——总比演一个残缺的名字强。
                if (!SlotMachine.IsUsable(names))
                {
                    return BuildMotion(RevealAnimationStyle.Scroll, names, winner, settings, pickIndexSelector, stopOffsetSelector);
                }

                // 格数 = 最长名字的字数（夹在 2~4），总时长按格数摊成「每格多少秒」。
                var slots = Math.Clamp(SlotMachine.LongestNameLength(names),
                    SlotMachine.MinSlots, SlotMachine.MaxSlots);
                var duration = AnimationGate.DurationOf(style, settings, slots);
                var step = TimeSpan.FromSeconds(duration.TotalSeconds / slots);
                return SlotMachine.Build(names, winner, step);
            }

            case RevealAnimationStyle.Wheel:
            {
                // 「滑进扇区」那一下是固定的，配置里的时长只管转到边界那一段。
                var duration = AnimationGate.DurationOf(style, settings) - WheelLayout.SlideDuration;
                return WheelLayout.Build(names, winner, duration,
                    pickIndexSelector: pickIndexSelector);
            }

            default:
                // 无动画（或将来新增了样式但这里忘了处理）：交给上层直接出结果。
                return null;
        }
    }
}
