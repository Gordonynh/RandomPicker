using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Views.Animations;

/// <summary>
/// 动画的挂载点：按排片表造出对应的动画控件，负责播放，也负责在取消时收干净。
/// </summary>
/// <remarks>
/// 这是整套动画<b>唯一</b>的异步入口。窗口那一侧只管「播这段」和「掐掉」两件事，
/// 不需要认识四种控件里的任何一个。
/// <para/>
/// <b>取消语义：</b>被取消时什么都不做——不留终值、不清空控件。
/// 「把进度硬置到终点」只在动画<b>正常播完</b>时做。理由是这个宿主会被反复复用：
/// 上一轮残留的终值会让下一轮的动画一上来就显示上一次的中选者，
/// 表现是「转盘歪着不动」「方块停在半路」，而且只在连点时出现，很难查。
/// </remarks>
internal sealed class RevealAnimationHost : Decorator
{
    /// <summary>当前挂着的动画控件。</summary>
    private RevealAnimationBase? _current;

    /// <summary>
    /// 按排片表装载一段动画，返回它要占的舞台尺寸。
    /// </summary>
    /// <param name="plan">排片表。</param>
    /// <param name="revealFontSize">中央大字的字号档位。</param>
    /// <param name="accent">主题强调色。</param>
    public Size Load(RevealAnimationPlan plan, double revealFontSize, Color accent)
    {
        // 按样式造出对应的控件。
        _current = plan switch
        {
            ScrollPlan => new ScrollNameAnimation(revealFontSize, accent),
            CsgoPlan => new CsgoAnimation(revealFontSize, accent),
            SlotPlan => new SlotMachineAnimation(revealFontSize, accent),
            WheelPlan => new WheelAnimation(revealFontSize, accent),
            // 喵~防御：将来新增了样式但这里忘了处理时，静默不播好过抛异常把结果卡住。
            _ => null
        };

        // 喵~防御：不认识的排片表——把上一轮的控件清掉，直接当作没有动画。
        if (_current is null)
        {
            Child = null;
            return default;
        }

        // 先把排片表装进去，舞台尺寸才是准的。
        _current.Load(plan);
        Child = _current;
        return _current.StageSize;
    }

    /// <summary>
    /// 播放当前装载的这段动画。
    /// </summary>
    /// <param name="token">取消令牌。</param>
    public async Task PlayAsync(CancellationToken token)
    {
        // 喵~防御：还没装载就播（调用顺序写错）时什么都不做。
        if (_current is null)
        {
            return;
        }

        // 先让出一轮：控件刚挂进可视树，等布局跑完再起动画，
        // 第一帧才不会画在一块 0×0 的空区域上（那样看起来就是「动画没播」）。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        await _current.PlayAsync(token);
    }

    /// <summary>把进度硬置到终点。只在动画正常播完时调用。</summary>
    public void SnapToEnd() => _current?.SnapToEnd();

    /// <summary>卸掉当前动画控件，断开引用。</summary>
    public void Reset()
    {
        Child = null;
        _current = null;
    }
}
