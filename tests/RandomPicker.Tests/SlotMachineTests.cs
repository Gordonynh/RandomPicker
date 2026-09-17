using System;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 老虎机样式的格数与候选字收敛测试。
/// </summary>
/// <remarks>
/// 最要命的一类错误出在「中选者比格数短」的时候：格数由名单里<b>最长</b>的名字决定，
/// 抽到的却可能是短名字。这时末尾几格必须留空，绝不能拿别人的字去填——
/// 填了就会演成一个和中选者对不上的板面，比不出动画还糟。
/// </remarks>
public sealed class SlotMachineTests
{
    /// <summary>每一格转多久。</summary>
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1.5);

    #region 名字长度

    [Fact]
    public void LongestNameLength_ReturnsTheLongestOne()
    {
        // 三个人里「李小四」最长，是三个字。
        Assert.Equal(3, SlotMachine.LongestNameLength(["张三", "李小四", "王五"]));
    }

    [Fact]
    public void LongestNameLength_WithEmptyRoster_ReturnsZero()
    {
        Assert.Equal(0, SlotMachine.LongestNameLength([]));
    }

    [Fact]
    public void LongestNameLength_WithNullRoster_ReturnsZero()
    {
        // 喵~防御：名单为 null 时不能崩。
        Assert.Equal(0, SlotMachine.LongestNameLength(null));
    }

    [Fact]
    public void IsUsable_WithFourCharacterName_IsTrue()
    {
        // 四个字正好是上限。
        Assert.True(SlotMachine.IsUsable(["张三", "欧阳修文"]));
    }

    [Fact]
    public void IsUsable_WithFiveCharacterName_IsFalse()
    {
        // 五个字超出上限，这个样式用不了（由上层回退到滚动名字）。
        Assert.False(SlotMachine.IsUsable(["张三", "司马相如字"]));
    }

    [Fact]
    public void IsUsable_WithEmptyRoster_IsFalse()
    {
        Assert.False(SlotMachine.IsUsable([]));
    }

    #endregion

    #region 格数

    [Fact]
    public void Build_SlotCountEqualsLongestNameLength()
    {
        // 格数跟着当前名单里最长的名字走。
        var plan = SlotMachine.Build(["张三", "李小四"], "李小四", Step);

        Assert.NotNull(plan);
        Assert.Equal(3, plan.SlotCount);
    }

    [Fact]
    public void Build_SlotCountMinIsTwo()
    {
        // 全是一两个字的名字时至少也画两格，一格不叫老虎机。
        var plan = SlotMachine.Build(["张三", "李四"], "张三", Step);

        Assert.NotNull(plan);
        Assert.Equal(SlotMachine.MinSlots, plan.SlotCount);
    }

    [Fact]
    public void Build_SlotCountMaxIsFour()
    {
        // 四个字的名字正好画四格。
        var plan = SlotMachine.Build(["张三", "欧阳修文"], "欧阳修文", Step);

        Assert.NotNull(plan);
        Assert.Equal(SlotMachine.MaxSlots, plan.SlotCount);
    }

    #endregion

    #region 候选字收敛

    [Fact]
    public void Build_CandidatesNarrowAsThePrefixIsSettled()
    {
        // 名单：张三、张四、李五。
        var plan = SlotMachine.Build(["张三", "张四", "李五"], "张三", Step);

        Assert.NotNull(plan);
        // 第一格：三人的第一个字，共「张、李」两个候选。
        Assert.Equal("张李", plan.Candidates[0]);
        // 第一格定下「张」之后，第二格只剩「三、四」——姓李的已经被排除。
        Assert.Equal("三四", plan.Candidates[1]);
    }

    [Fact]
    public void Build_CandidatesAreSortedForStableOrder()
    {
        // 候选字排过序，测试断言才是稳定的（不然每次跑出来的顺序都不一样）。
        var plan = SlotMachine.Build(["王五", "张三", "李四"], "王五", Step);

        Assert.NotNull(plan);
        // 「张」「李」「王」按码点排是 张 < 李 < 王。
        Assert.Equal("张李王", plan.Candidates[0]);
    }

    [Fact]
    public void Build_WinnerCharIsAlwaysAmongTheCandidates()
    {
        // 每一格最终停下的那个字必须在该格的候选集合里，否则「停在一个不可能的字上」。
        var plan = SlotMachine.Build(["张三", "张四", "李五"], "张四", Step);

        Assert.NotNull(plan);
        for (var slot = 0; slot < plan.SlotCount; slot++)
        {
            if (plan.WinnerChars[slot] is { } character)
            {
                Assert.Contains(character, plan.Candidates[slot]);
            }
        }
    }

    [Fact]
    public void Build_WinnerCharsSpellOutTheWinner()
    {
        // 各格的字拼起来就是中选者的名字。
        var plan = SlotMachine.Build(["张三", "李小四"], "李小四", Step);

        Assert.NotNull(plan);
        Assert.Equal('李', plan.WinnerChars[0]);
        Assert.Equal('小', plan.WinnerChars[1]);
        Assert.Equal('四', plan.WinnerChars[2]);
    }

    #endregion

    #region 短名字留下的空格

    [Fact]
    public void Build_ShortWinner_LeavesTrailingSlotsBlank()
    {
        // 名单里有三个字的名字，所以画三格；可抽到的是两个字的「张三」。
        var plan = SlotMachine.Build(["张三", "李小四"], "张三", Step);

        Assert.NotNull(plan);
        Assert.Equal(3, plan.SlotCount);
        // 前两格是「张」「三」。
        Assert.Equal('张', plan.WinnerChars[0]);
        Assert.Equal('三', plan.WinnerChars[1]);
        // 第三格必须留空——不能拿「小」或「四」去填，那会演成另一个人的名字。
        Assert.Null(plan.WinnerChars[2]);
    }

    [Fact]
    public void Build_AllSingleCharacterNames_SecondSlotIsBlank()
    {
        // 名单全是单字名时格数抬到 2，但中选者只有一个字，第二格留空。
        var plan = SlotMachine.Build(["张", "李"], "张", Step);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.SlotCount);
        Assert.Equal('张', plan.WinnerChars[0]);
        Assert.Null(plan.WinnerChars[1]);
    }

    [Fact]
    public void Build_BlankSlotDoesNotHideALongerWinner()
    {
        // 反过来：中选者是最长的那个人时不该出现空格。
        var plan = SlotMachine.Build(["张三", "李小四"], "李小四", Step);

        Assert.NotNull(plan);
        Assert.All(plan.WinnerChars, character => Assert.NotNull(character));
    }

    #endregion

    #region 时长

    [Fact]
    public void Build_TotalIsJustTheSpinTime()
    {
        // 配置里写的是「每格多少秒」，动作时长就是它乘上<b>要转的格数</b>
        // （不是总格数——候选字只剩唯一的那些格子不占时间）。
        // 收尾那段定格也不在这儿加，由 RevealAnimationPlanner 统一给四种样式补上，
        // 这样四个样式的收尾节奏必然是同一个值，不会漏掉某一个。
        var plan = SlotMachine.Build(["张三", "李小四"], "李小四", Step);

        Assert.NotNull(plan);
        Assert.Equal(Step, plan.Step);
        // 名字最长三个字 → 三格。但中选者姓李，第一格定下「李」之后，
        // 后面两格只剩「小」「四」两种唯一可能 → 它们不转。
        Assert.Equal(3, plan.SlotCount);
        Assert.Equal(1, plan.SpinSlotCount);
        Assert.Equal(Step, plan.Total);
    }

    [Fact]
    public void Build_SpinSlotCountStopsAtTheFirstAllDeterminedSlot()
    {
        // 两个人的姓不同 → 第一格必须转（候选是「张」「李」）。
        var firstSlotMatters = SlotMachine.Build(["张三", "李四"], "张三", Step);

        Assert.NotNull(firstSlotMatters);
        Assert.Equal(1, firstSlotMatters.SpinSlotCount);

        // 四个名字，前三个字就分出胜负时也是同样的道理。
        // 但这一条要注意：规则是「<b>从某一格起往后全都唯一</b>」才提前收尾，
        // 不是「某一格自己唯一就不转」。这里最后一格「文/武」有两种可能，
        // 所以从第一格起就找不到这样的位置，四格都得转。
        var lastSlotStillOpen = SlotMachine.Build(["欧阳修文", "欧阳修武"], "欧阳修文", Step);

        Assert.NotNull(lastSlotStillOpen);
        Assert.Equal(4, lastSlotStillOpen.SpinSlotCount);
    }

    [Fact]
    public void Build_WhenNoSlotHasASecondPossibility_NothingSpins()
    {
        // 名单是「张」和「张三」：第一个字定下「张」之后，后面全是唯一的，
        // 整段动作时长为 0——看见的是板面直接出现、停一下、出结果。
        var plan = SlotMachine.Build(["张", "张三"], "张三", Step);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.SpinSlotCount);
        Assert.Equal(TimeSpan.Zero, plan.Total);
    }

    [Fact]
    public void Build_WithZeroStep_UsesFallback()
    {
        // 喵~防御：单格时长被改成 0 时不能一闪而过，兜底成 1 秒。
        var plan = SlotMachine.Build(["张三", "李四"], "张三", TimeSpan.Zero);

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.Step);
    }

    #endregion

    #region 边界与防御

    [Fact]
    public void Build_WithNullRoster_ReturnsNull()
    {
        Assert.Null(SlotMachine.Build(null, "张三", Step));
    }

    [Fact]
    public void Build_WithEmptyRoster_ReturnsNull()
    {
        Assert.Null(SlotMachine.Build([], "张三", Step));
    }

    [Fact]
    public void Build_WithNullWinner_ReturnsNull()
    {
        Assert.Null(SlotMachine.Build(["张三", "李四"], null, Step));
    }

    [Fact]
    public void Build_WhenWinnerNotInRoster_ReturnsNull()
    {
        // 动画开播前名单被改过：中选者已经不在了，不能演成别人的名字。
        Assert.Null(SlotMachine.Build(["张三", "李四"], "查无此人", Step));
    }

    #endregion
}
