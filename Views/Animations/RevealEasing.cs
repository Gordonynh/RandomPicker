using Avalonia.Animation.Easings;

namespace ClassIsland.RandomPicker.Views.Animations;

/// <summary>
/// 四种抽选动画共用的缓动曲线。
/// </summary>
/// <remarks>
/// 统一放在这里，是为了让四种样式的「快慢手感」一模一样——
/// 用户逐个试过去的时候，感觉到的是同一种节奏，只是表演形式不同。
/// <para/>
/// <b>全部是非线性的「先快后慢」</b>：起手冲出去、收尾一点点挪。
/// 匀速的线性缓动在抽选动画里很难看——它没有「减速刹住」的暗示，
/// 用户会怀疑动画是不是卡住了。
/// </remarks>
internal static class RevealEasing
{
    /// <summary>
    /// 主减速曲线：起手快、收尾慢，用来「滚过去再停住」。
    /// </summary>
    /// <remarks>
    /// 三次方 ease-out。比二次方更利落，收尾那一下有明确的「刹住」感；
    /// 又比四次方温柔，不会快得像瞬移。
    /// </remarks>
    public static Easing Decelerate { get; } = new CubicEaseOut();

    /// <summary>
    /// 收尾滑入用的曲线：带一点回弹，像被磁铁吸进去。
    /// </summary>
    /// <remarks>
    /// 只用在转盘「从缝里滑进扇区」那半格，以及将来别的「最后一下对上位置」的动作。
    /// 回弹幅度很小（默认振幅），不会甩到隔壁格子上去。
    /// </remarks>
    public static Easing Settle { get; } = new BackEaseOut();
}
