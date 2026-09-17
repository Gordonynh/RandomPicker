using Avalonia;
using Avalonia.Headless;
using ClassIsland.RandomPicker.Tests;

// 让 xUnit 在这个程序集里用 Avalonia 的无头平台跑测试。
// 没有这一行，任何碰控件的测试都会在「平台还没初始化」上直接失败。
[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 测试用的 Avalonia 应用。
/// </summary>
/// <remarks>
/// <b>为什么要有这个：</b>插件的界面层（窗口构造、自绘控件的度量与绘制）在编译期完全不设防。
/// 2026-09-15 就踩过一次真实的坑——给结果层加的 <c>DoubleTransition</c> 漏写了 <c>Property</c>，
/// 编译 0 错误、197 个纯逻辑测试全绿，但用户一装上去点第一下就抛
/// 「Transition has no property specified.」，插件被宿主自动禁用。
/// <para/>
/// 这套无头环境就是用来堵这个洞的：不用显示器也能把窗口和控件真的建出来、量出来、画出来。
/// </remarks>
public static class HeadlessTestApp
{
    /// <summary>建立测试用的应用实例。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            // UseHeadlessDrawing = false：不用「什么都不画」的假绘制后端。
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // 换成真的 Skia，RenderTargetBitmap 才有像素可读，文字也才量得出宽度。
            .UseSkia();
}
