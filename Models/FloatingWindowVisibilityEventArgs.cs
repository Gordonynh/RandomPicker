using System;

namespace ClassIsland.RandomPicker.Models;

/// <summary>悬浮窗显隐状态变化事件参数。</summary>
public class FloatingWindowVisibilityEventArgs : EventArgs
{
    /// <summary>true 表示悬浮窗刚刚显示，false 表示刚刚隐藏。</summary>
    public bool IsVisible { get; init; }
}
