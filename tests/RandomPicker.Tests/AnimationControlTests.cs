using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views;
using ClassIsland.RandomPicker.Views.Animations;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 界面层的测试：窗口建得出来、动画控件量得出尺寸、画得出东西。
/// </summary>
/// <remarks>
/// <b>这批测试是补一个真实事故的洞。</b>
/// 2026-09-15，给结果层加的 <c>DoubleTransition</c> 漏写了 <c>Property</c>：
/// 编译 0 错误，197 个纯逻辑测试全绿，但用户装上后点第一下就抛
/// 「Transition has no property specified.」，插件被宿主自动禁用。
/// <para/>
/// 那类问题只在「真的把控件建出来、量出来、画出来」的那一刻才现形，
/// 所以这里用 Avalonia 的无头平台把它们全都跑一遍。
/// 无头环境不需要显示器，但要求 Skia 在场（见 <see cref="HeadlessTestApp"/>），
/// 否则渲染出来的位图恒为空白，「有没有画出东西」这条断言就失去意义了。
/// </remarks>
public class AnimationControlTests
{
    /// <summary>测试用的名单。名字长度不齐，是为了让老虎机走出「末尾留空」那条路。</summary>
    private static readonly string[] Names = ["张三", "李小四", "王五"];

    /// <summary>一份默认设置，用来取字号档位——动画控件的尺寸换算都以它为基准。</summary>
    private static readonly PickerSettings BaseSettings = new();

    /// <summary>按指定样式造一份插件设置。</summary>
    /// <remarks>
    /// 字号不在这里设：<c>RevealFontSize</c> 是由悬浮窗大小档位推导出来的只读属性，
    /// 想改字号得改「大小」，测试里没这个必要。
    /// </remarks>
    private static PickerSettings SettingsFor(RevealAnimationStyle style) =>
        new() { AnimationStyle = style };

    /// <summary>
    /// 每种样式都要能源自排片表建出控件，并且量出一个正的舞台尺寸。
    /// </summary>
    /// <remarks>
    /// 裸 <c>Control</c> 默认量出 0×0——画面上一片空白<b>而且不报任何错</b>，
    /// 是最难查的一类问题。这条断言把它钉死。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(RevealAnimationStyle.Scroll)]
    [InlineData(RevealAnimationStyle.Csgo)]
    [InlineData(RevealAnimationStyle.Slot)]
    [InlineData(RevealAnimationStyle.Wheel)]
    public void EveryStyle_MeasuresPositiveStageSize(RevealAnimationStyle style)
    {
        // 按样式造排片表。造不出来就说明这个用例本身有问题，直接失败。
        var plan = RevealAnimationPlanner.Build(style, Names, "张三", SettingsFor(style));
        Assert.NotNull(plan);

        // 按计划造控件并装载。
        var control = CreateControl(style);
        control.Load(plan);

        // 舞台尺寸必须是正数，否则布局会把它压成一条线。
        Assert.True(control.StageSize.Width > 0, $"宽度应大于 0，实际是 {control.StageSize.Width}");
        Assert.True(control.StageSize.Height > 0, $"高度应大于 0，实际是 {control.StageSize.Height}");
    }

    /// <summary>
    /// 每种样式定格之后都要真的往位图上画出东西。
    /// </summary>
    /// <remarks>
    /// 这是「度量正确」之外的另一半：几何算对了不代表画得出来。
    /// <c>Render</c> 里只要有一处尺寸换算成 NaN，Skia 那边就整帧空白，而异常往往被
    /// 渲染循环吞掉——只有把像素读出来看才验得了。
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(RevealAnimationStyle.Scroll)]
    [InlineData(RevealAnimationStyle.Csgo)]
    [InlineData(RevealAnimationStyle.Slot)]
    [InlineData(RevealAnimationStyle.Wheel)]
    public void EveryStyle_DrawsVisiblePixels(RevealAnimationStyle style)
    {
        // 造排片表并装载。
        var plan = RevealAnimationPlanner.Build(style, Names, "张三", SettingsFor(style));
        Assert.NotNull(plan);

        var control = CreateControl(style);
        control.Load(plan);
        // 定格到终点：这是用户最终看到的那一帧，最该画得出来。
        control.SnapToEnd();

        // 渲染成位图并检查有没有像素。
        var bitmap = RenderToBitmap(control);
        Assert.True(HasAnyVisiblePixel(bitmap),
            $"{style} 定格后整张位图是空的——控件没画出任何东西");
    }

    /// <summary>
    /// 还没装载就渲染、以及装载了错误的计划类型，都不该抛异常。
    /// </summary>
    /// <remarks>
    /// 动画控件的调用顺序由宿主保证，但「什么都还没装就先画了一帧」是布局阶段真实会发生的事。
    /// 这时候必须安静地什么都不画，而不是把异常抛进渲染循环。
    /// </remarks>
    [AvaloniaFact]
    public void Render_BeforeLoad_DoesNotThrow()
    {
        foreach (var style in new[] { RevealAnimationStyle.Scroll, RevealAnimationStyle.Csgo,
                     RevealAnimationStyle.Slot, RevealAnimationStyle.Wheel })
        {
            var control = CreateControl(style);
            // 故意不调用 Load。
            RenderToBitmap(control);
        }
    }

    /// <summary>
    /// 装载错的计划类型时不抛异常，只是什么都不画。
    /// </summary>
    [AvaloniaFact]
    public void Load_WrongPlanType_DoesNotThrow()
    {
        // 拿一份滚动名字的计划，喂给另外三种控件。
        var scrollPlan = RevealAnimationPlanner.Build(
            RevealAnimationStyle.Scroll, Names, "张三", SettingsFor(RevealAnimationStyle.Scroll));
        Assert.NotNull(scrollPlan);

        foreach (var style in new[] { RevealAnimationStyle.Csgo, RevealAnimationStyle.Slot,
                     RevealAnimationStyle.Wheel })
        {
            var control = CreateControl(style);
            control.Load(scrollPlan);
            // 计划类型不匹配，舞台尺寸应该是零，但绝不该抛异常。
            RenderToBitmap(control);
        }
    }

    /// <summary>按样式造出对应的动画控件。字号用默认设置推导出来的那个，和实际运行时一致。</summary>
    private static RevealAnimationBase CreateControl(RevealAnimationStyle style)
    {
        // 动画控件的尺寸换算都以这个字号为基准。
        var fontSize = BaseSettings.RevealFontSize;
        return style switch
        {
            RevealAnimationStyle.Csgo => new CsgoAnimation(fontSize, Colors.Cyan),
            RevealAnimationStyle.Slot => new SlotMachineAnimation(fontSize, Colors.Cyan),
            RevealAnimationStyle.Wheel => new WheelAnimation(fontSize, Colors.Cyan),
            // 剩下的（含 Scroll）都用滚动名字。
            _ => new ScrollNameAnimation(fontSize, Colors.Cyan)
        };
    }

    /// <summary>把一个控件量好、摆好，再渲染成位图。</summary>
    private static RenderTargetBitmap RenderToBitmap(RevealAnimationBase control)
    {
        var size = control.StageSize;
        // 喵~防御：舞台尺寸可能是零（装载失败），位图至少要有一个像素才建得出来。
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height));

        // 先把布局跑一遍：不量不摆的话 Bounds 是 0×0，渲染出来必然是空的。
        control.Measure(size);
        control.Arrange(new Rect(0, 0, size.Width, size.Height));

        var bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
        bitmap.Render(control);
        return bitmap;
    }

    /// <summary>位图里有没有任何一个非全零的像素。</summary>
    /// <remarks>
    /// 只判断「有没有画出东西」，不比对具体颜色——颜色随主题强调色变，
    /// 比对颜色会让测试变得很脆，而这里要挡的是「整帧空白」。
    /// </remarks>
    private static bool HasAnyVisiblePixel(RenderTargetBitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        // 每行字节数：每个像素 4 字节（BGRA 或 RGBA，这里不区分）。
        var stride = width * 4;
        var buffer = new byte[stride * height];

        // 用固定句柄把托管数组的地址交给 Skia 去写，省得开 unsafe。
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height),
                handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return buffer.Any(value => value != 0);
    }

    /// <summary>取位图上某个像素的 alpha（0 = 全透明，255 = 全不透明）。</summary>
    /// <param name="bitmap">要读的位图。</param>
    /// <param name="x">横坐标。</param>
    /// <param name="y">纵坐标。</param>
    private static byte AlphaAt(RenderTargetBitmap bitmap, int x, int y)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var buffer = new byte[stride * height];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height),
                handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // 每个像素 4 个字节，最后一个是不透明度。
        return buffer[(y * width + x) * 4 + 3];
    }

    /// <summary>
    /// CSGO：舞台的四角必须是空的——不许铺一层铺满整块的背景。
    /// </summary>
    /// <remarks>
    /// <b>这条是为 2026-09-16「csgo 不小心把全局背景颜色也改成品质色了」建的。</b>
    /// 那层背景铺满整个舞台、还跟着品质一路换色，滚动的时候整屏一闪一闪，
    /// 把真正要看的那排方块都压住了。主人要求撤掉。
    /// <para/>
    /// 方块只占纵向中间那一行，所以四角理应全透明。哪天有人再把整块背景加回来，
    /// 四角的 alpha 就不再是 0，这条立刻会红——比靠肉眼看出来可靠得多。
    /// </remarks>
    [AvaloniaFact]
    public void CsgoAnimation_LeavesTheStageCornersEmpty()
    {
        // 造一份真实的排片表并装进控件。
        var plan = RevealAnimationPlanner.Build(RevealAnimationStyle.Csgo, Names, "张三",
            SettingsFor(RevealAnimationStyle.Csgo));
        Assert.NotNull(plan);

        var control = new CsgoAnimation(BaseSettings.RevealFontSize, Colors.Cyan);
        control.Load(plan);
        // 推到终点：中选方块高亮、品质色最浓，这是画面元素最多的时候。
        control.SnapToEnd();

        var bitmap = RenderToBitmap(control);
        var right = bitmap.PixelSize.Width - 1;
        var bottom = bitmap.PixelSize.Height - 1;

        // 四个角都该是全透明的。
        Assert.Equal(0, AlphaAt(bitmap, 0, 0));
        Assert.Equal(0, AlphaAt(bitmap, right, 0));
        Assert.Equal(0, AlphaAt(bitmap, 0, bottom));
        Assert.Equal(0, AlphaAt(bitmap, right, bottom));

        // 对照：中间那一行确实画了东西，不然上面那几条「全透明」可能是因为压根没渲染。
        Assert.True(HasAnyVisiblePixel(bitmap), "整张位图是空的，这条测试等于没测");
    }
}
