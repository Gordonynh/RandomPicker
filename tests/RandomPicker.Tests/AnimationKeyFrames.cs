using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 读关键帧的小工具，几个动画测试共用。
/// </summary>
/// <remarks>
/// 动画控件把「怎么动」都做成了关键帧，测试要验的就是这些关键帧——
/// 时刻对不对、值走到哪儿、有没有留白。这些读法写一遍就够，不该在每个测试类里抄一份。
/// </remarks>
internal static class AnimationKeyFrames
{
    /// <summary>挑出某个属性上的所有关键帧，按原顺序返回「时刻 + 值」。</summary>
    /// <param name="animation">要读的动画。</param>
    /// <param name="property">只挑写这个属性的关键帧。</param>
    /// <remarks>
    /// 关键帧里装的是 <c>IAnimationSetter</c>，它的 <c>Property</c>/<c>Value</c> 在插件这一侧不可访问
    /// （接口成员不是 public），所以要落到具体的 <see cref="Setter"/> 上再读。
    /// 喵~防御：值不是 double 的（类型写错）直接跳过，不硬转。
    /// </remarks>
    public static (TimeSpan Time, double Value)[] FramesOf(Animation animation, AvaloniaProperty property)
    {
        // 喵~防御：还没造出动画时没什么可读的。
        if (animation is null)
        {
            return [];
        }

        var frames = new List<(TimeSpan Time, double Value)>();
        foreach (var keyFrame in animation.Children)
        {
            foreach (var setter in keyFrame.Setters)
            {
                if (setter is Setter concrete
                    && concrete.Property == property
                    && concrete.Value is double value)
                {
                    frames.Add((keyFrame.KeyTime, value));
                }
            }
        }

        return [.. frames];
    }

    /// <summary>所有关键帧里最晚的那个时刻。多段动画按时长依次排开，调用方自己加上前面各段。</summary>
    /// <remarks>用来验「动作确实在排片表说的那一刻停下」。</remarks>
    public static TimeSpan LatestKeyTime(Animation animation)
    {
        // 喵~防御：空动画没有时刻可言。
        if (animation is null)
        {
            return TimeSpan.Zero;
        }

        var latest = TimeSpan.Zero;
        foreach (var keyFrame in animation.Children)
        {
            if (keyFrame.KeyTime > latest)
            {
                latest = keyFrame.KeyTime;
            }
        }

        return latest;
    }
}
