using System;
using System.IO;
using System.Text;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 动画时长解析的测试，外加一条「谁在管动画开不开」的政策守卫。
/// </summary>
/// <remarks>
/// 这里原来有一组「按 ClassIsland 的动画等级降级」的测试。2026-09-15 主人决定改政策：
/// <b>插件动画不再受宿主的「动画级别」管制</b>——动画是这个功能本身，不是界面装饰，
/// ClassIsland 关掉自己的界面过渡不该连累抽选表演。想彻底关掉，在右键菜单里选「无动画」。
/// 于是那一组测试连着被测的 <c>AnimationGate.Resolve</c> 一起删了，
/// 换成下面两条：一条钉「唯一的关闭方式是选无动画」，
/// 一条用二进制扫描钉「插件里不许再出现 IThemeService」。
/// </remarks>
public sealed class AnimationGateTests
{
    /// <summary>一个默认设置，各测试按需改写其中一两项。</summary>
    private static PickerSettings DefaultSettings => new();

    /// <summary>测试用名单。</summary>
    private static readonly string[] Names = ["张三", "李四", "王五"];

    #region 谁能关掉动画

    /// <summary>
    /// 唯一能关掉动画的是插件自己的「无动画」，另外四种都一定造得出排片表。
    /// </summary>
    /// <remarks>
    /// 这条是上面那段政策在行为上的样子：没有任何外部状态参与判断，
    /// 所以「选了某一种却不播」只可能是数据不满足该样式（比如老虎机遇上超长名字），
    /// 而不可能是宿主那边某个开关悄悄把动画掐了。
    /// </remarks>
    [Fact]
    public void OnlyTheNoneChoiceTurnsTheAnimationOff()
    {
        var settings = DefaultSettings;

        // 选「无动画」→ 没有排片表，直接出结果。
        Assert.Null(RevealAnimationPlanner.Build(RevealAnimationStyle.None, Names, "张三", settings));

        // 其余四种 → 一定造得出排片表。
        foreach (var style in new[]
                 {
                     RevealAnimationStyle.Scroll, RevealAnimationStyle.Csgo,
                     RevealAnimationStyle.Slot, RevealAnimationStyle.Wheel
                 })
        {
            Assert.NotNull(RevealAnimationPlanner.Build(style, Names, "张三", settings));
        }
    }

    /// <summary>
    /// 插件程序集的元数据里不许再出现 <c>IThemeService</c>。
    /// </summary>
    /// <remarks>
    /// 用二进制扫描而不是反射，是因为反射看不见方法体里的引用——
    /// 只把 <c>IThemeService.AnimationLevel</c> 写回某个方法里，反射是查不出来的。
    /// 而类型名一定会落在程序集的元数据字符串堆里，扫得到。
    /// <para/>
    /// 「动画级别」这个判据曾经写错过一次（记成「等级 &gt;= 2」，默认安装下动画永远不播），
    /// 后来整条路都按主人的决定拆掉了。这条守卫的作用是：谁想把它接回来，先得删掉这条测试。
    /// </remarks>
    [Fact]
    public void NothingInThePluginMentionsTheHostsAnimationService()
    {
        // 被测程序集就是这个插件本体，测试运行时它就在测试输出目录里。
        var dllPath = typeof(PickerSettings).Assembly.Location;
        Assert.True(File.Exists(dllPath), $"找不到插件程序集：{dllPath}");

        var bytes = File.ReadAllBytes(dllPath);
        var needle = Encoding.UTF8.GetBytes("IThemeService");

        Assert.False(ContainsBytes(bytes, needle),
            "插件里又出现了对宿主 IThemeService 的引用。动画只该由插件自己的设置决定——" +
            "要接回「尊重宿主的动画级别」，先想清楚为什么 2026-09-15 那条决定不成立。");
    }

    /// <summary>在大段字节里找一小段字节，找到返回 true。</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        // 喵~防御：空针会让下面的循环永远匹配，直接当作找不到。
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            // 先比第一个字节再比其余，绝大多数位置会在第一步就被排除。
            if (haystack[i] != needle[0])
            {
                continue;
            }

            var matched = true;
            for (var j = 1; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region 时长解析

    [Fact]
    public void DurationOf_Scroll_UsesConfiguredSeconds()
    {
        var settings = new PickerSettings { ScrollDurationSeconds = 7.5 };

        Assert.Equal(TimeSpan.FromSeconds(7.5), AnimationGate.DurationOf(RevealAnimationStyle.Scroll, settings));
    }

    [Fact]
    public void DurationOf_Scroll_ClampsOutOfRangeValues()
    {
        // 配置被改成荒谬的值时夹回区间，免得动画只有 0 秒或者拖到几分钟。
        Assert.Equal(TimeSpan.FromSeconds(0.5),
            AnimationGate.DurationOf(RevealAnimationStyle.Scroll, new PickerSettings { ScrollDurationSeconds = -3 }));
        Assert.Equal(TimeSpan.FromSeconds(20),
            AnimationGate.DurationOf(RevealAnimationStyle.Scroll, new PickerSettings { ScrollDurationSeconds = 999 }));
    }

    [Fact]
    public void DurationOf_WithNaNSetting_FallsBackToMinimum()
    {
        // 喵~防御：NaN 传进 Math.Clamp 会被原样放出来，时长就成了 NaN，
        // 动画永远结束不了。这里必须退回到最短时长。
        var duration = AnimationGate.DurationOf(RevealAnimationStyle.Csgo,
            new PickerSettings { CsgoDurationSeconds = double.NaN });

        Assert.Equal(TimeSpan.FromSeconds(0.5), duration);
    }

    [Fact]
    public void DurationOf_Slot_ScalesWithSlotCount()
    {
        // 老虎机的时间配置是「每格多少秒」，总时长要乘格数。
        var settings = new PickerSettings { SlotStepSeconds = 1.5 };

        Assert.Equal(TimeSpan.FromSeconds(6), AnimationGate.DurationOf(RevealAnimationStyle.Slot, settings, slotCount: 4));
        Assert.Equal(TimeSpan.FromSeconds(3), AnimationGate.DurationOf(RevealAnimationStyle.Slot, settings, slotCount: 2));
    }

    [Fact]
    public void DurationOf_Slot_WithoutSlotCount_AssumesMinimumSlots()
    {
        // 调用方还没算出格数时，按最少格数给一个像样的时长，而不是 0。
        var settings = new PickerSettings { SlotStepSeconds = 1.5 };

        Assert.Equal(TimeSpan.FromSeconds(3), AnimationGate.DurationOf(RevealAnimationStyle.Slot, settings));
    }

    [Fact]
    public void DurationOf_Slot_ClampsOutOfRangeSlotCount()
    {
        // 格数被传成 0 或 99 时夹回 2~4。
        var settings = new PickerSettings { SlotStepSeconds = 1.0 };

        Assert.Equal(TimeSpan.FromSeconds(2), AnimationGate.DurationOf(RevealAnimationStyle.Slot, settings, slotCount: -5));
        Assert.Equal(TimeSpan.FromSeconds(4), AnimationGate.DurationOf(RevealAnimationStyle.Slot, settings, slotCount: 99));
    }

    [Fact]
    public void DurationOf_Wheel_AddsTheFixedSlideSegment()
    {
        // 转盘的配置只管「转到边界」，滑进扇区那一下是固定的，要额外加上。
        var settings = new PickerSettings { WheelDurationSeconds = 6 };

        Assert.Equal(TimeSpan.FromSeconds(6) + WheelLayout.SlideDuration,
            AnimationGate.DurationOf(RevealAnimationStyle.Wheel, settings));
    }

    [Fact]
    public void DurationOf_None_IsZero()
    {
        // 无动画不花时间。
        Assert.Equal(TimeSpan.Zero, AnimationGate.DurationOf(RevealAnimationStyle.None, DefaultSettings));
    }

    [Fact]
    public void DurationOf_WithNullSettings_Throws()
    {
        // 喵~防御：设置对象不允许为 null，早报错好定位。
        Assert.Throws<ArgumentNullException>(() =>
            AnimationGate.DurationOf(RevealAnimationStyle.Scroll, null!));
    }

    #endregion

    #region 排片表工厂

    [Fact]
    public void Planner_BuildsScrollPlan()
    {
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Scroll,
            ["张三", "李四", "王五"], "李四", new PickerSettings());

        var scroll = Assert.IsType<ScrollPlan>(plan);
        // 最后一帧必须是中选者——动画定格在他身上。
        Assert.Equal("李四", scroll.Frames[^1]);
        Assert.Equal("李四", scroll.Winner);
    }

    [Fact]
    public void Planner_BuildsCsgoPlan()
    {
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Csgo,
            ["张三", "李四", "王五"], "王五", new PickerSettings());

        var csgo = Assert.IsType<CsgoPlan>(plan);
        // 指针底下那一块必须就是中选者。
        Assert.Equal("王五", csgo.Track[csgo.WinnerTrackIndex]);
    }

    [Fact]
    public void Planner_BuildsSlotPlan()
    {
        // 三个字的名字 → 3 个格子。
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot,
            ["张三", "李小四"], "李小四", new PickerSettings());

        var slot = Assert.IsType<SlotPlan>(plan);
        Assert.Equal(3, slot.SlotCount);
        Assert.Equal("李小四", slot.Winner);
    }

    [Fact]
    public void Planner_Slot_WithTooLongName_FallsBackToScroll()
    {
        // 名单里有超过 4 个字的名字时老虎机显示不完整，自动回退到滚动名字。
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot,
            ["张三", "司马相如字长卿"], "张三", new PickerSettings());

        Assert.IsType<ScrollPlan>(plan);
    }

    [Fact]
    public void Planner_BuildsWheelPlan()
    {
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Wheel,
            ["张三", "李四", "王五"], "张三", new PickerSettings());

        var wheel = Assert.IsType<WheelPlan>(plan);
        // 最终角度必须指向中选者那一格。
        Assert.Equal(wheel.WinnerSector, WheelLayout.SectorUnderPointer(wheel.FinalAngle, wheel.Sectors.Count));
    }

    [Fact]
    public void Planner_WithNone_ReturnsNull()
    {
        // 「无动画」不生成计划，上层据此直接出结果。
        Assert.Null(RevealAnimationPlanner.Build(RevealAnimationStyle.None,
            ["张三", "李四"], "张三", new PickerSettings()));
    }

    [Fact]
    public void Planner_WithEmptyRoster_ReturnsNull()
    {
        Assert.Null(RevealAnimationPlanner.Build(RevealAnimationStyle.Scroll,
            [], "张三", new PickerSettings()));
    }

    [Fact]
    public void Planner_WithNullWinner_ReturnsNull()
    {
        Assert.Null(RevealAnimationPlanner.Build(RevealAnimationStyle.Scroll,
            ["张三"], null, new PickerSettings()));
    }

    [Fact]
    public void Planner_WithNullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RevealAnimationPlanner.Build(RevealAnimationStyle.Scroll, ["张三"], "张三", null!));
    }

    #endregion
}
