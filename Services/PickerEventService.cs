using System;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 抽选事件的转接点，也是外部调用（URL / HTTP / 自动化动作）请求抽选的入口。
/// </summary>
/// <remarks>
/// 为什么需要它：触发器是 <c>AddKeyedTransient</c> 建出来的短生命周期对象，
/// 行动是另一批短生命周期对象，悬浮窗服务又是 <c>IHostedService</c>——
/// 谁都不该直接抓着谁。这个单例只做两件事：广播「抽完了」、转发「请抽一次」。
/// <para/>
/// <see cref="PickExecutor"/> 由 <see cref="PickerHostService"/> 在启动时挂上，
/// 这样行动和 URL 不需要反向依赖宿主服务，也不会形成 DI 循环。
/// </remarks>
public sealed class PickerEventService
{
    private readonly object _gate = new();

    /// <summary>抽选完成后触发。</summary>
    public event EventHandler<PickEventArgs>? Picked;

    /// <summary>最近一次抽选结果，规则集读它。</summary>
    public PickEventArgs? LastPick { get; private set; }

    /// <summary>执行抽选的委托，由宿主服务在 StartAsync 里挂上。</summary>
    public Action<PickRequest>? PickExecutor { get; set; }

    /// <summary>悬浮窗显示 / 隐藏状态变化时触发（自动化「当隐藏时 / 当显示时」触发器订阅它）。</summary>
    public event EventHandler<FloatingWindowVisibilityEventArgs>? FloatingWindowVisibilityChanged;

    /// <summary>发布一次抽选结果。</summary>
    public void Publish(PickEventArgs args)
    {
        lock (_gate)
        {
            LastPick = args;
        }

        Picked?.Invoke(this, args);
    }

    /// <summary>请求抽一次。宿主还没就绪时静默忽略。</summary>
    public void Request(PickRequest request) => PickExecutor?.Invoke(request);

    /// <summary>广播悬浮窗显隐状态变化。</summary>
    public void PublishFloatingWindowVisibility(bool visible)
    {
        FloatingWindowVisibilityChanged?.Invoke(this, new FloatingWindowVisibilityEventArgs { IsVisible = visible });
    }
}
