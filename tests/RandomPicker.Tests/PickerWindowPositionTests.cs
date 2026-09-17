using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 悬浮窗「位置什么时候才允许存盘」的测试。
/// </summary>
/// <remarks>
/// <b>这个文件是为 2026-09-16「开机后悬浮钮跑到左上角、只重启 ClassIsland 又正常」建的。</b>
/// 那个毛病是两环扣在一起的：
/// <list type="number">
/// <item>开机那几秒屏幕信息还没就绪，自动摆位一次都成功不了，窗口停在系统给的 (0,0)；</item>
/// <item>退出时 <c>CapturePosition</c> 把 (0,0) 当成「主人的位置」存进了配置——
///       下次开机读到 (0,0)、继续存 (0,0)，就此卡死在左上角，自己永远好不了。</item>
/// </list>
/// 单独重启 ClassIsland 时屏幕信息立刻可用，第一环不成立，所以看不出问题。
/// <para/>
/// 第一环归 <see cref="WindowPlacement"/> 管（那边有单测钉着「信息不可信就别动窗口」），
/// 这里钉的是第二环：<b>位置没被确认过之前，一个字都不许写进配置。</b>
/// 无头平台足够验这一条——它验的是「什么情况下才写」，不是「写出来是多少」。
/// </remarks>
public sealed class PickerWindowPositionTests : IDisposable
{
    /// <summary>本测试独享的临时目录。名单服务会真的读写文件，共用目录会互相干扰。</summary>
    private readonly string _temporaryDirectory;

    public PickerWindowPositionTests()
    {
        // 目录名带随机后缀，并行跑测试时两个实例不会撞在一起。
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "RandomPickerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    public void Dispose()
    {
        try
        {
            // 递归删掉本测试产生的临时文件。
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 文件监视还占着句柄时删不掉，留给系统自己清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上，权限不足时也不该让测试失败。
        }
    }

    /// <summary>造一个悬浮窗，配一份全新的配置。</summary>
    private (PickerWindow Window, PickerSettings Settings) CreateWindow()
    {
        // 全新的配置：位置字段还是「没记过」的 int.MinValue。
        var settings = new PickerSettings();
        // 名单服务要一个真实路径，随便给临时目录里一个名字就行。
        var roster = new RosterService(Path.Combine(_temporaryDirectory, "名单.txt"));
        return (new PickerWindow(settings, roster), settings);
    }

    /// <summary>
    /// 摆位还没结束之前，<c>CapturePosition</c> 一个字都不许写进配置。
    /// </summary>
    /// <remarks>
    /// <b>这一条就是那个 bug 的根。</b>窗口刚建出来时停在系统给的 (0,0)，
    /// 这时候存盘就等于把 (0,0) 当成主人的位置记住了——下次开机摆到左上角、继续存 (0,0)，
    /// 从此自己好不了，只有主人手动拖一次才能恢复。
    /// </remarks>
    [AvaloniaFact]
    public void CapturePosition_BeforePlacementSettles_DoesNotWriteAnything()
    {
        var (window, settings) = CreateWindow();

        // 窗口还没开过，摆位阶段自然还没结束。
        window.CapturePosition();

        // 配置里依然是「没记过位置」——一个字都没写进去。
        Assert.Equal(int.MinValue, settings.WindowX);
        Assert.Equal(int.MinValue, settings.WindowY);
        // 屏幕尺寸同理，一并看住。
        Assert.Equal(int.MinValue, settings.WindowScreenWidth);
        Assert.Equal(int.MinValue, settings.WindowScreenHeight);
    }

    /// <summary>
    /// 摆位阶段走完之后，位置才允许写进配置。
    /// </summary>
    /// <remarks>
    /// 光有上面那条不够：如果把 <c>CapturePosition</c> 写成「永远不记」，
    /// 那条测试照样绿，可位置就再也存不下来了。这一条是它的反面。
    /// </remarks>
    [AvaloniaFact]
    public void CapturePosition_AfterTheWindowIsOpened_WritesThePosition()
    {
        var (window, settings) = CreateWindow();

        window.Show();
        // 开窗那一串活排在同一条队列上，跑完才算摆位结束。
        Dispatcher.UIThread.RunJobs();

        // 摆位失败时窗口停在 (0,0)，那种情况不算「确认过」，依旧不许记——
        // 所以这里先确认摆位确实成功了（窗口不在原点）。
        Assert.True(window.Position.X != 0 || window.Position.Y != 0,
            "无头平台没能提供可用的屏幕信息，这条测试的前提不成立");

        window.CapturePosition();

        // 位置写进去了，而且写的就是窗口当前的位置。
        Assert.Equal(window.Position.X, settings.WindowX);
        Assert.Equal(window.Position.Y, settings.WindowY);
    }
}
