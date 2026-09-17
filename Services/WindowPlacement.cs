using System;
using System.Collections.Generic;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 一块屏幕矩形，单位是物理像素。
/// </summary>
/// <remarks>
/// 故意不用 Avalonia 的 <c>PixelRect</c>：这一层是纯计算，不依赖界面框架，
/// 才好在没有显示器的环境里直接跑单元测试（和 <see cref="FaceGeometry"/>、<see cref="WheelLayout"/> 一个路子）。
/// </remarks>
/// <param name="X">左边界。</param>
/// <param name="Y">上边界。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
internal readonly record struct ScreenArea(int X, int Y, int Width, int Height);

/// <summary>
/// 悬浮钮的摆位计算。
/// </summary>
/// <remarks>
/// 从 <c>PickerWindow</c> 里抽出来，是因为这里出过一个真实的 bug：
/// <b>系统重启后悬浮钮会跑到屏幕左上角</b>。
/// <para/>
/// 原因是开窗那一刻显示器信息可能还没就绪——拿到的是空矩形，或者缩放比例是个坏值。
/// 照常算下去，<c>Math.Clamp</c> 的合法区间会塌成屏幕原点那一个点，
/// 窗口就被硬拽到 (0,0)。关键在于这类坏值在正常使用中永远不会出现，
/// 只有开机那一瞬间才有，所以只能把判断本身拿出来单测。
/// <para/>
/// 返回 <c>null</c> 一律表示「这次算不准，别动窗口」，而不是「算出来在原点」。
/// 这是刻意的：位置不动只是不好看，钉死在左上角是坏掉。
/// </remarks>
internal static class WindowPlacement
{
    /// <summary>缩放比例的下限。低于它说明这个值不可信。</summary>
    public const double MinScaling = 0.25;

    /// <summary>缩放比例的上限。高于它说明这个值不可信（比如把百分比当成了倍数）。</summary>
    public const double MaxScaling = 8.0;

    /// <summary>首次运行时，悬浮钮离屏幕边角留出的空隙，单位：逻辑像素。</summary>
    public const int CornerMargin = 48;

    /// <summary>缩放比例是不是个可信的值。</summary>
    public static bool IsScalingSane(double scaling) =>
        // 喵~防御：NaN 和 Infinity 参与任何比较都是 false，会一路漏到乘法和取整里去。
        double.IsFinite(scaling) && scaling >= MinScaling && scaling <= MaxScaling;

    /// <summary>这块屏幕加上这个缩放比例，能不能拿来算位置。</summary>
    public static bool IsUsable(ScreenArea area, double scaling) =>
        // 喵~防御：宽或高是 0（甚至负数）说明显示器信息还没探到。
        area.Width > 0 && area.Height > 0 && IsScalingSane(scaling);

    /// <summary>
    /// 记下位置时的那块屏幕，现在还找得到吗？
    /// </summary>
    /// <param name="recordedWidth">记下位置时那块屏幕的宽度，物理像素。</param>
    /// <param name="recordedHeight">高度，物理像素。</param>
    /// <param name="screens">当前所有屏幕。</param>
    /// <returns>还有任意一块屏幕的尺寸和记录的一致时返回 true。</returns>
    /// <remarks>
    /// 返回 false 表示分辨率变过了（换了显示器、改了分辨率、接了投影仪），
    /// 记下来的坐标在新屏幕上未必还有意义，调用方应当回到默认位置。
    /// <para/>
    /// <b>判据是「任意一块对得上」，不是「当前这块对得上」。</b>
    /// 双屏时窗口摆在副屏上，开机那一瞬间窗口还在原点，
    /// 按当前屏幕算会拿主屏的尺寸去比副屏记下的值——什么都没变，位置却被重置了。
    /// <para/>
    /// 喵~防御：没记录过尺寸（<see cref="int.MinValue"/>）时返回 true。
    /// 老版本的配置文件里只有坐标、没有尺寸，那种情况按「没变过」处理，
    /// 免得升级一次就把主人摆好的位置重置掉。
    /// </remarks>
    public static bool MatchesAnyScreen(int recordedWidth, int recordedHeight,
        IReadOnlyList<ScreenArea>? screens)
    {
        if (recordedWidth == int.MinValue || recordedHeight == int.MinValue)
        {
            return true;
        }

        // 喵~防御：一块屏幕都拿不到（显示器信息还没就绪）时当作「还判断不了」，
        // 返回 true 让调用方留着原位置——位置没摆对只是不好看，重置错了是坏掉。
        if (screens is null || screens.Count == 0)
        {
            return true;
        }

        foreach (var screen in screens)
        {
            if (screen.Width == recordedWidth && screen.Height == recordedHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把窗口位置夹进屏幕里。
    /// </summary>
    /// <param name="position">想要摆到的位置，物理像素。</param>
    /// <param name="bounds">屏幕矩形，物理像素。</param>
    /// <param name="scaling">屏幕缩放比例。</param>
    /// <param name="logicalWidth">窗口宽度，逻辑像素。</param>
    /// <param name="logicalHeight">窗口高度，逻辑像素。</param>
    /// <returns>夹取后的位置；本次算不准时返回 <c>null</c>，表示「不要动窗口」。</returns>
    public static (int X, int Y)? Clamp((int X, int Y) position, ScreenArea bounds, double scaling,
        double logicalWidth, double logicalHeight)
    {
        // 喵~防御：屏幕信息不可信时返回 null。照常算的话下面 maxX 会等于 bounds.X，
        // 位置被钉在屏幕左上角——这正是那个重启后跑到左上角的 bug。
        if (!IsUsable(bounds, scaling))
        {
            return null;
        }

        // 逻辑像素换成物理像素。
        var width = (int)Math.Ceiling(logicalWidth * scaling);
        var height = (int)Math.Ceiling(logicalHeight * scaling);

        // 喵~防御：窗口比屏幕还大时下界会大于上界，Math.Clamp 会把位置钉死在屏幕原点。
        // 这种情况宁可不夹。
        if (width >= bounds.Width || height >= bounds.Height)
        {
            return null;
        }

        // 能摆到的最大坐标：再往右/往下就出屏了。
        var maxX = bounds.X + bounds.Width - width;
        var maxY = bounds.Y + bounds.Height - height;

        return (Math.Clamp(position.X, bounds.X, maxX), Math.Clamp(position.Y, bounds.Y, maxY));
    }

    /// <summary>
    /// 首次运行时该摆在哪：屏幕右下角靠里一点。
    /// </summary>
    /// <param name="workingArea">屏幕的工作区矩形（不含任务栏），物理像素。</param>
    /// <param name="scaling">屏幕缩放比例。</param>
    /// <param name="diameter">悬浮钮直径，逻辑像素。</param>
    /// <returns>算好的位置；本次算不准时返回 <c>null</c>。</returns>
    public static (int X, int Y)? DefaultCorner(ScreenArea workingArea, double scaling, double diameter)
    {
        // 喵~防御：同上，信息不可信就不摆。
        if (!IsUsable(workingArea, scaling))
        {
            return null;
        }

        // 悬浮钮的物理像素尺寸。
        var size = (int)Math.Ceiling(diameter * scaling);
        // 边距也跟着缩放，高分屏上看起来才一样宽。
        var margin = (int)(CornerMargin * scaling);

        return (workingArea.X + workingArea.Width - size - margin,
                workingArea.Y + workingArea.Height - size - margin);
    }
}
