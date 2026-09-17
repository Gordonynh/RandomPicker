using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views;
using ClassIsland.RandomPicker.Views.Animations;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 动画<b>真的会播</b>的测试。
/// </summary>
/// <remarks>
/// <b>这个文件是为 2026-09-15「动画并没有触发」建的。</b>
/// 滚动名字那个控件建 <c>Animation</c> 时漏了 <c>Duration</c>，而 Avalonia 拿它当分母
/// 把每个关键帧的 <c>KeyTime</c> 换算成 cue（<c>cue = KeyTime / Duration</c>）：
/// 默认值是 <c>TimeSpan.Zero</c>，第一个关键帧算出 <c>0/0 = NaN</c>，
/// <c>Cue</c> 的构造函数当场抛异常，动画在起播的那一瞬间就失败了。
/// 那个异常被 <c>RevealWindow</c> 里「绝不吞掉结果」的兜底接住，
/// 于是症状是<b>结果照出、动画全无、还不报错</b>。
/// <para/>
/// 为什么原有测试全绿：<c>AnimationControlTests</c> 只验「量得出尺寸」和「画得出像素」——
/// 这两件事在<b>装载</b>阶段就完成了，和「时间轴跑不跑得起来」是两码事；
/// <c>RevealWindowTests.Play_WithRealPlan_DoesNotThrow</c> 又只看「起播那一下不抛异常」，
/// 而异常是<b>异步</b>发生的，早就被吞在后面了。
/// 这里补的就是这道缝：既要<b>结构上</b>验时长和时刻表对得上，
/// 也要<b>行为上</b>验「时钟没走，结果就不许出来」。
/// </remarks>
public class AnimationPlaybackTests
{
    /// <summary>测试用名单。</summary>
    private static readonly string[] Names = ["张三", "李小四", "王五"];

    /// <summary>一份默认设置，用来取字号档位。</summary>
    private static readonly PickerSettings BaseSettings = new();

    /// <summary>结果停留时长，测试里取短一点。</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1);

    #region 结构：时长和时刻表必须对得上

    /// <summary>
    /// 四种样式共同的时间不变量：总时长 = 动作 + 定格，动作在排片表说的那一刻停下，
    /// 每个关键帧的 cue 都落在 0~1 里。
    /// </summary>
    /// <remarks>
    /// 一次把四种样式都验掉，是因为这几条性质<b>本来就该对每个样式都成立</b>：
    /// <list type="number">
    /// <item><c>总时长 - 动作结束时刻 == 定格时长</c>——少了它最后一帧会被结果顶掉；</item>
    /// <item><c>各段 Duration 之和 == 动作时长</c>——转盘是两段，其余是一段；</item>
    /// <item><c>关键帧的最大时刻 == 动作结束时刻</c>——动作确实在排片表说的那一刻停；</item>
    /// <item>每个 cue 都在 0~1 里——Avalonia 拿 <c>KeyTime / Duration</c> 当 cue，
    ///       越界会当场抛异常（某次漏写 <c>Duration</c> 就是这么炸的）；</item>
    /// <item>每一段的 <c>Duration</c> 都大于零——零会让 cue 变成 <c>0/0</c>。</item>
    /// </list>
    /// 分散成四个样式各写一遍的话，早晚漏掉一个——这个 bug 已经漏过两次了。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(RevealAnimationStyle.Scroll)]
    [InlineData(RevealAnimationStyle.Csgo)]
    [InlineData(RevealAnimationStyle.Slot)]
    [InlineData(RevealAnimationStyle.Wheel)]
    public void EveryStyle_KeepsTheSameTimingInvariants(RevealAnimationStyle style)
    {
        // 造一份真实的排片表。
        var settings = new PickerSettings { AnimationStyle = style };
        var plan = RevealAnimationPlanner.Build(style, Names, "张三", settings);
        Assert.NotNull(plan);

        // ① 总时长里确实留了定格，且长度正好是设定值。
        Assert.True(plan.Total > plan.MotionTotal,
            $"{style}: 总时长里没有留下定格，最后一帧会被结果顶掉");
        Assert.Equal(TimeSpan.FromSeconds(RevealAnimationPlan.SettleSeconds),
            plan.Total - plan.MotionTotal);

        // 造控件并装载。
        var control = CreateControl(style);
        control.Load(plan);
        var animations = control.BuildAnimations();
        Assert.NotEmpty(animations);

        // ② 各段 Duration 之和 == 动作时长（转盘两段、其余一段）。
        // 定格不算在内：它由控件的基类在播完之后原地等，不占动画时长。
        // 把定格算进 Duration 会让关键帧的 cue 整体缩短——CSGO 那次就是这么坏的，
        // 终点 cue 从 1.0 掉到 0.8，减速曲线在 0.8 处已经走完 99%，整段减速被挤进前 41%。
        var sum = TimeSpan.Zero;
        foreach (var animation in animations)
        {
            // ⑤ 每一段的时长都必须为正——零会让 Avalonia 算出 0/0。
            Assert.True(animation.Duration > TimeSpan.Zero,
                $"{style}: 有动画段的时长为零，Avalonia 拿它当分母算 cue，会在起播瞬间失败");
            sum += animation.Duration;
        }

        Assert.Equal(plan.MotionTotal, sum);

        // ③④ 逐段把关键帧换算成「绝对时刻」和 cue，两者都必须在合法范围里。
        // 多段是**依次**播的，所以后面那段的时刻要加上前面各段的时长。
        var elapsedBefore = TimeSpan.Zero;
        var latestKeyTime = TimeSpan.Zero;
        foreach (var animation in animations)
        {
            Assert.NotEmpty(animation.Children);
            foreach (var keyFrame in animation.Children)
            {
                // ④ cue 落在 0~1 里，和 Avalonia 的算法同源。
                Assert.InRange(CueOf(keyFrame, animation.Duration), 0.0, 1.0);
                // 关键帧的绝对时刻。
                var absolute = elapsedBefore + keyFrame.KeyTime;
                if (absolute > latestKeyTime)
                {
                    latestKeyTime = absolute;
                }
            }

            elapsedBefore += animation.Duration;
        }

        // ③ 最后一段动作确实铺到了动作时长的末尾。分两种情形：
        //    · CSGO / 老虎机 / 转盘的终点帧本身就是终值，它落在末尾；
        //    · 滚动名字的末帧是「<b>开始显示</b>中选者」的那一刻，之后还要停它自己那一格，
        //      所以「末帧时刻 + 末帧停留」才该等于末尾。
        // 后一种写法顺带钉住了一个真出过的 bug：写关键帧时把「先累加再取值」的顺序弄反了，
        // 每一帧占用的都是下一帧的时长——中选者那格本该停最久，却被倒数第二个名字占掉，
        // 他一出现就进定格（2026-09-15 主人说的「他最后变慢的过程似乎消失了」）。
        // 那样算出来的「末帧时刻 + 末帧停留」会多出整整一格，这条断言当场就红。
        if (plan is ScrollPlan scrollPlan)
        {
            // 末帧自己那一格停留多久。
            var lastFrameDwell = TimeSpan.FromSeconds(scrollPlan.Intervals[^1]);
            // 逐项换算成 TimeSpan 会取整到 tick，攒下来的零头允许 1 毫秒的误差。
            var gap = (plan.MotionTotal - (latestKeyTime + lastFrameDwell)).Duration();

            Assert.True(gap < TimeSpan.FromMilliseconds(1),
                $"{style}: 末帧时刻 {latestKeyTime} 加上它那一格 {lastFrameDwell} 之后"
                + $"离动作末尾 {plan.MotionTotal} 还差 {gap}");
        }
        else
        {
            Assert.Equal(plan.MotionTotal, latestKeyTime);
        }
    }

    /// <summary>按样式造出对应的动画控件。</summary>
    private static RevealAnimationBase CreateControl(RevealAnimationStyle style) => style switch
    {
        RevealAnimationStyle.Csgo => new CsgoAnimation(BaseSettings.RevealFontSize, Colors.Cyan),
        RevealAnimationStyle.Slot => new SlotMachineAnimation(BaseSettings.RevealFontSize, Colors.Cyan),
        RevealAnimationStyle.Wheel => new WheelAnimation(BaseSettings.RevealFontSize, Colors.Cyan),
        // 剩下的（含 Scroll）都用滚动名字。
        _ => new ScrollNameAnimation(BaseSettings.RevealFontSize, Colors.Cyan)
    };

    /// <summary>
    /// 不给 <c>Duration</c> 的动画，算出来的 cue 一定越界，<c>Cue</c> 会抛异常。
    /// </summary>
    /// <remarks>
    /// <b>这条是故意写来「证伪」的</b>：它把那个 bug 的机理钉在测试里，
    /// 让后来的人一眼看清「少写一行 Duration」为什么会要命，
    /// 而不是只看到上面那条断言、不明白它防的是什么。
    /// </remarks>
    [AvaloniaFact]
    public void AnimationWithoutDuration_ProducesOutOfRangeCues()
    {
        // 复现当时的写法：只给 KeyTime，不给 Duration。
        var broken = new Animation { FillMode = FillMode.Forward, Easing = new LinearEasing() };
        broken.Children.Add(new KeyFrame
        {
            KeyTime = TimeSpan.Zero,
            Setters = { new Setter(ScrollNameAnimation.ProgressProperty, 0.0) }
        });
        broken.Children.Add(new KeyFrame
        {
            KeyTime = TimeSpan.FromSeconds(4),
            Setters = { new Setter(ScrollNameAnimation.ProgressProperty, 10.0) }
        });

        // Duration 默认就是零秒。
        Assert.Equal(TimeSpan.Zero, broken.Duration);

        // 照抄 Avalonia 的换算：KeyTime / Duration。
        // 第一个关键帧是 0/0 = NaN，第二个是 4/0 = 正无穷，两个都不在 0~1 里。
        Assert.Throws<ArgumentException>(() => _ = new Cue(broken.Children[0].KeyTime.TotalSeconds
                                                          / broken.Duration.TotalSeconds));
        Assert.Throws<ArgumentException>(() => _ = new Cue(broken.Children[1].KeyTime.TotalSeconds
                                                          / broken.Duration.TotalSeconds));
    }

    /// <summary>
    /// 老虎机：最后一格必须在「动作结束时刻」停住，不算最后一个重播的候选字。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「老虎机最后的抽取有问题，应该最后一个字展示一会会再继续」建的。</b>
    /// 当时的排片表把总时长定成「每格 × 格数」，最后一格恰好在总时长那一刻才停住，
    /// 动画当场结束就切去出结果，拼好的名字一帧都留不下。
    /// 现在定格由 <see cref="RevealAnimationPlan"/> 统一提供，这条专门盯着老虎机那一段的落点。
    /// </remarks>
    [AvaloniaFact]
    public void SlotAnimation_LandsTheLastSpinSlotOnTheMotionEnd()
    {
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot, SlotStepSeconds = 1.5 };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot, Names, "张三", settings);
        var slot = Assert.IsType<SlotPlan>(plan);

        // 排片表这一层：动作时长 = 每格 × 要转的格数。
        Assert.Equal(slot.Step * slot.SpinSlotCount, slot.MotionTotal);

        // 动画这一层：关键帧的最大时刻就是动作结束时刻，落到它为止都在转，之后才是定格。
        var control = new SlotMachineAnimation(BaseSettings.RevealFontSize, Colors.Cyan);
        control.Load(slot);
        var animations = control.BuildAnimations();
        var animation = Assert.Single(animations);

        var latest = TimeSpan.Zero;
        foreach (var keyFrame in animation.Children)
        {
            if (keyFrame.KeyTime > latest)
            {
                latest = keyFrame.KeyTime;
            }
        }

        // 最后一格的落点正好是动作结束时刻——不能早（会少转一会儿），也不能晚（会把定格吃掉）。
        Assert.Equal(slot.MotionTotal, latest);
    }

    /// <summary>
    /// 老虎机：候选字只剩唯一的那几格不转，动画直接把它们亮出来。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「老虎机最后候选已经只有唯一，是直接展示结果并且结束动画」建的。</b>
    /// 名单里所有名字都同姓时，第一格就已经只剩一个候选字，再转也只是同一个字一直闪——
    /// 那段时间白等，还会让人以为画面卡住了。
    /// </remarks>
    [Fact]
    public void SlotMachine_StopsSpinningOnceEveryRemainingSlotIsTheOnlyPossibility()
    {
        // 「张三、李四」：第一格有「张」「李」两种可能，得转；
        // 但中选者姓张，第一格定下之后第二格只剩「三」，没有第二种可能了。
        // 于是只有第一格要转，后面那格直接亮出来，动画比原来早一步收尾。
        var twoNames = SlotMachine.Build(["张三", "李四"], "张三", TimeSpan.FromSeconds(1));

        Assert.NotNull(twoNames);
        Assert.Equal(2, twoNames.SlotCount);
        Assert.Equal(1, twoNames.SpinSlotCount);
        // 动作时长 = 每格 × 要转的格数，而不是 × 总格数。
        Assert.Equal(TimeSpan.FromSeconds(1), twoNames.Total);

        // 连第一格都没得抽时（名单里有「张」又有「张三」）：整段动作时长为 0，
        // 看见的是「板面直接出现、停一下、出结果」。
        var nothingToSpin = SlotMachine.Build(["张", "张三"], "张三", TimeSpan.FromSeconds(1));

        Assert.NotNull(nothingToSpin);
        Assert.Equal(0, nothingToSpin.SpinSlotCount);
        Assert.Equal(TimeSpan.Zero, nothingToSpin.Total);

        // 反例：最后一格还有两种可能时，一格都不能省——「提前收尾」要求的是
        // 「从某一格起往后全都唯一」，不是「某一格自己唯一」。
        var lastSlotOpen = SlotMachine.Build(["张三", "张四"], "张三", TimeSpan.FromSeconds(1));

        Assert.NotNull(lastSlotOpen);
        Assert.Equal(2, lastSlotOpen.SpinSlotCount);
    }

    /// <summary>
    /// 老虎机：候选只剩唯一的那几格，必须等到别格停稳的那一刻才亮，不能一开场就显示。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是剧透。</b>名单「张三、李四」抽中张三时，第一格有「张」「李」两种可能（要转），
    /// 但第一格一旦定下「张」，第二格就只剩「三」。要是第二格一开场就把「三」摆出来，
    /// 第一格还在滚的时候答案就已经漏了——「抽」的过程也就没意义了。
    /// </remarks>
    [AvaloniaFact]
    public void SlotAnimation_KeepsTheOnlyPossibilitySlotsHiddenUntilTheSpinStops()
    {
        // 走真实的那条路造排片表（planner 会补上定格），别手搓——手搓的没有定格那一段，
        // MotionTotal 就不是「动作结束时刻」了。
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot, SlotStepSeconds = 1 };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Slot, ["张三", "李四"], "张三", settings);
        var slot = Assert.IsType<SlotPlan>(plan);
        // 第一格要转，第二格只剩唯一。
        Assert.Equal(1, slot.SpinSlotCount);

        var control = new SlotMachineAnimation(BaseSettings.RevealFontSize, Colors.Cyan);
        control.Load(slot);
        var animation = Assert.Single(control.BuildAnimations());

        // 第二格（唯一候选的那一格）：先空着，到动作结束才亮。
        var secondSlot = AnimationKeyFrames.FramesOf(animation, SlotMachineAnimation.Slot1ProgressProperty);

        Assert.NotEmpty(secondSlot);
        // 最早的画面必须是空的（进度 0），而且不能落在时刻 0 上——那等于开场就亮。
        Assert.Equal(0.0, secondSlot[0].Value);
        Assert.True(secondSlot[0].Time > TimeSpan.Zero,
            "唯一候选的那格在开场就亮了，答案会提前泄露");
        // 亮出来的时刻正好是动作结束时刻，和前面那格停稳是同一瞬间。
        Assert.Equal(1.0, secondSlot[^1].Value);
        Assert.Equal(slot.MotionTotal, secondSlot[^1].Time);

        // 对照：真正要转的第一格是从时刻 0 就开始的。
        var firstSlot = AnimationKeyFrames.FramesOf(animation, SlotMachineAnimation.Slot0ProgressProperty);

        Assert.NotEmpty(firstSlot);
        Assert.Equal(TimeSpan.Zero, firstSlot[0].Time);
        Assert.Equal(0.0, firstSlot[0].Value);
        Assert.Equal(slot.MotionTotal, firstSlot[^1].Time);
        Assert.Equal(1.0, firstSlot[^1].Value);
    }

    /// <summary>
    /// CSGO：终点关键帧必须落在动画的<b>最后一刻</b>（cue = 1），减速曲线才铺得满。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「csgo 减速过程消失了」建的。</b>
    /// 当时终点被钉在「总时长 - 定格」上，cue 因此只有 0.8；而
    /// <c>CubicEaseOut</c> 在 0.8 处已经走完了 99%（<c>1-0.2³ ≈ 0.992</c>），
    /// 也就是滚动在动画的第 41% 就冲完了，后面一大截全是干等——
    /// 可辨认的减速被整个挤没了。
    /// 现在定格交给基类在播完之后等，动画时长就是动作时长，终点 cue 必须是 1。
    /// </remarks>
    [AvaloniaFact]
    public void CsgoAnimation_LandsTheEndKeyFrameOnTheVeryEnd()
    {
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Csgo };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Csgo, Names, "张三", settings);
        Assert.NotNull(plan);

        var control = new CsgoAnimation(BaseSettings.RevealFontSize, Colors.Cyan);
        control.Load(plan);
        var animation = Assert.Single(control.BuildAnimations());

        // 动画时长 = 动作时长，定格不占这里的时长。
        Assert.Equal(plan.MotionTotal, animation.Duration);

        // 终点关键帧落在最后一刻，cue 正好是 1——缓动曲线因此能铺满整段。
        Assert.NotEmpty(animation.Children);
        Assert.Equal(1.0, CueOf(animation.Children[^1], animation.Duration), 9);

        // 用的确实是那条减速曲线（起手快、收尾一点点挪）。
        Assert.IsAssignableFrom<CubicEaseOut>(animation.Easing);
    }

    /// <summary>
    /// CSGO 的停点是随机的：指针不必每次都压在方块正中，但底下必须还是中选者那一块。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「真实情况下停在中间概率很低」建的。</b>
    /// 每次都精确居中一眼假——真实开箱是连续刹停的，指针落在方块内的哪个位置本来就随机。
    /// 但随机只能改变「停在方块的哪个位置」，绝不能改变「停在哪一块」。
    /// </remarks>
    [Fact]
    public void CsgoTrack_StopOffsetMovesTheStopButNeverTheWinner()
    {
        string[] roster = ["张三", "李四", "王五"];
        var offsets = new List<double>();

        // 沿着 0~1 均匀取几个停点，每个都验一遍。
        foreach (var roll in new[] { 0.0, 0.25, 0.5, 0.75, 0.999 })
        {
            var plan = CsgoTrack.Build(roster, "李四", 400, 120, 60, 80, 8,
                TimeSpan.FromSeconds(4), ItemRarity.Covert, null, _ => 0, () => roll);
            Assert.NotNull(plan);

            // 指针底下仍然是中选者那一块。
            var under = CsgoTrack.IndexAtOffset(plan.TargetOffset, plan.SlotWidth, plan.Gap,
                plan.ViewportWidth);
            Assert.Equal(plan.WinnerTrackIndex, under);

            // 偏移量不超过规定的上限——越过了指针就会指到隔壁那一块上。
            var centered = CsgoTrack.OffsetFor(plan.WinnerTrackIndex, plan.SlotWidth, plan.Gap,
                plan.ViewportWidth);
            var jitter = Math.Abs(plan.TargetOffset - centered);
            Assert.True(jitter <= plan.SlotWidth * CsgoTrack.MaxStopJitterRatio + 1e-6,
                $"停点偏了 {jitter}，超过了 {plan.SlotWidth * CsgoTrack.MaxStopJitterRatio} 的上限");

            offsets.Add(plan.TargetOffset);
        }

        // 停点确实随随机数在变——不是每次都停在同一处。
        Assert.True(offsets.Distinct().Count() > 1, "停点没有随随机数变化，等于还是每次都停在同一个位置");
    }

    /// <summary>
    /// 转盘：点亮的那一格必须永远是指针指着的那一格，绝不提前亮出中选者。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-15「转盘如果回转的话会有剧透」建的。</b>
    /// 原来点亮的是「中选者那一格」再加一个「停稳了没有」的判断，而第二段的滑动方向是两种之一：
    /// 其中一种会让指针先落在中选者<b>旁边</b>那一格上、再滑进中选者。
    /// 那段时间里指针还没到，中选者却已经亮着了——答案提前泄露。
    /// 现在高亮只跟着指针走，两者在任何角度下都必须一致。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void WheelAnimation_HighlightAlwaysFollowsThePointer(int boundaryPick)
    {
        // 固定「停在哪条缝上」，把滑动方向钉成确定的：0 表示往一侧滑、1 表示往另一侧滑。
        // 不固定的话这一盘的几何随机，断言时有时无，等于没测。
        var settings = new PickerSettings { AnimationStyle = RevealAnimationStyle.Wheel };
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Wheel,
            ["张三", "李四", "王五", "赵六"], "王五", settings, _ => boundaryPick);
        var wheel = Assert.IsType<WheelPlan>(plan);

        var control = new WheelAnimation(BaseSettings.RevealFontSize, Colors.Cyan);
        control.Load(wheel);

        // 从缝上的角度一路滑到终点，逐点检查高亮和指针是不是同一格。
        var from = wheel.BoundaryAngle;
        var to = wheel.FinalAngle;
        // 滑动途中是否出现过「指针还不在中选者身上」的时刻。
        var sawPointerOffTheWinner = false;

        for (var step = 0; step <= 100; step++)
        {
            var angle = from + (to - from) * step / 100.0;
            control.Angle = angle;

            // 高亮的那一格 == 指针指着的那一格。这就是防剧透的全部内容。
            Assert.Equal(WheelLayout.SectorUnderPointer(angle, wheel.Sectors.Count),
                control.HighlightedSectorIndex);

            if (control.HighlightedSectorIndex != wheel.WinnerSector)
            {
                sawPointerOffTheWinner = true;
            }
        }

        // 停稳时指针一定落在中选者身上，高亮自然也在他身上。
        control.Angle = to;
        Assert.Equal(wheel.WinnerSector, control.HighlightedSectorIndex);

        // 其中一个方向确实存在「指针还没滑到中选者」的阶段。
        // 这一盘才有「提前亮答案」的风险，上面那条比对才真的验到了东西；
        // 另一个方向指针全程都在中选者身上，没有可泄露的信息。
        if (wheel.SlideDirection > 0)
        {
            Assert.True(sawPointerOffTheWinner,
                "这个方向下滑入途中指针一直没离开中选者，说明几何和预期不符，这条测试等于没测");
        }
    }

    /// <summary>控件的定格时长从排片表来，而不是各自写死一个数。</summary>
    /// <remarks>
    /// 四个样式各写一遍的话，漏掉哪个哪个就不定格，而症状只是「最后一帧被结果顶掉」，
    /// 不报任何错。统一抄排片表，这一条把「抄到了」钉住。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(RevealAnimationStyle.Scroll)]
    [InlineData(RevealAnimationStyle.Csgo)]
    [InlineData(RevealAnimationStyle.Slot)]
    [InlineData(RevealAnimationStyle.Wheel)]
    public void Load_TakesTheSettleDurationFromThePlan(RevealAnimationStyle style)
    {
        var settings = new PickerSettings { AnimationStyle = style };
        var plan = RevealAnimationPlanner.Build(style, Names, "张三", settings);
        Assert.NotNull(plan);

        var control = CreateControl(style);
        control.Load(plan);

        // 定格时长就是排片表上那一段，而且确实是设定的值。
        Assert.Equal(plan.Settle, control.Settle);
        Assert.Equal(TimeSpan.FromSeconds(RevealAnimationPlan.SettleSeconds), control.Settle);
    }

    /// <summary>按 Avalonia 的算法，把一个关键帧换算成 cue。</summary>
    /// <remarks>关键帧写的是绝对时刻（<c>KeyFrameTimingMode.TimeSpan</c>），所以要除以总时长。</remarks>
    private static double CueOf(KeyFrame keyFrame, TimeSpan duration) =>
        keyFrame.KeyTime.TotalSeconds / duration.TotalSeconds;

    #endregion

    #region 行为：时钟没走，结果不许出来

    /// <summary>
    /// 四种样式都一样：排片表交出去之后，动画没跑完就不许把结果交出来。
    /// </summary>
    /// <remarks>
    /// 这条是为「动画没播还看不出来」补的。当时的 bug 会让动画<b>立刻失败</b>，
    /// 失败被吞掉之后收尾照走，结果马上就出来了——从外面看就是「动画并没有触发」。
    /// 所以这里把调度器上排着的活全干完，再断言结果<b>还没</b>出来：
    /// 时钟一点没走，谁也交不出结果。
    /// <para/>
    /// 时长特意取到上限（20 秒）：无头平台的时钟即使被推得很快，
    /// 也不可能在几十次空转里跑完 20 秒，这条断言因此是稳的。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(RevealAnimationStyle.Scroll)]
    [InlineData(RevealAnimationStyle.Csgo)]
    [InlineData(RevealAnimationStyle.Slot)]
    [InlineData(RevealAnimationStyle.Wheel)]
    public async Task Play_DoesNotDeliverTheResultBeforeTheAnimationRuns(RevealAnimationStyle style)
    {
        // 把这一段拉长，让「还没播完」这件事在测试里足够稳定。
        var settings = new PickerSettings
        {
            AnimationStyle = style,
            ScrollDurationSeconds = 20,
            CsgoDurationSeconds = 20,
            SlotStepSeconds = 10,
            WheelDurationSeconds = 20
        };

        var plan = RevealAnimationPlanner.Build(style, Names, "张三", settings);
        Assert.NotNull(plan);

        var completed = false;
        RevealWindow.Play(plan, settings.RevealFontSize, Colors.Cyan, Hold, () => completed = true);

        // 把调度器上排着的活干完：动画的起播续体就在这条队列上。
        // 起播如果失败（比如当年的 cue 越界），收尾会立刻跟着跑完——那样下面这条断言就会红。
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Assert.False(completed, $"{style} 的动画还没播就把结果交出来了，说明动画起播就失败了");

        RevealWindow.CloseCurrent();
    }

    #endregion
}
