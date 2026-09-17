using System;
using System.IO;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 插件主体的设置广播测试。
/// </summary>
/// <remarks>
/// <b>这一条是为「悬浮钮右键菜单和设置页显示不一致」建的。</b>
/// 两处改的是同一份设置，但各自只管刷新自己：从菜单改完动画样式，
/// 已经打开着的设置页不会知道，下拉框还停在旧的一档上。
/// 修法是让落盘时广播一次，设置页收到就重读。
/// <para/>
/// 这里只验「广播确实发出去了」——设置页那一侧的刷新需要加载 XAML 和真窗口，
/// 那部分交给无头界面测试那边覆盖。
/// </remarks>
public sealed class PickerHostServiceTests : IDisposable
{
    /// <summary>测试用的临时配置目录。</summary>
    private readonly string _configFolder =
        Path.Combine(Path.GetTempPath(), "RandomPickerTest_" + Guid.NewGuid().ToString("N"));

    /// <summary>把设置存盘时应该广播出去。</summary>
    /// <remarks>
    /// 少了这次广播，设置页就永远刷不到菜单改过的值——表现就是主人报的那个「两处不一致」。
    /// </remarks>
    [Fact]
    public void SaveSettings_BroadcastsSettingsSaved()
    {
        var service = new PickerHostService(_configFolder);
        var notified = 0;
        service.SettingsSaved += (_, _) => notified++;

        service.SaveSettings();

        Assert.Equal(1, notified);
    }

    /// <summary>广播发出去的时候，新值必须已经生效、也已经落盘。</summary>
    /// <remarks>
    /// 顺序很要紧：设置页收到广播会立刻回去读值。要是广播在赋值之前、或落盘之前发出去，
    /// 设置页读到的就是旧值，或者下次重启又变回去——症状和主人报的一模一样。
    /// </remarks>
    [Fact]
    public void SaveSettings_BroadcastsAfterTheValueAndTheFileAreUpdated()
    {
        var service = new PickerHostService(_configFolder);

        // 模拟从右键菜单改了动画样式。
        service.Settings.AnimationStyle = RevealAnimationStyle.Wheel;

        // 广播到达时，从「设置页那一侧的视角」记下看到的东西。
        RevealAnimationStyle? seenByListener = null;
        var fileHadIt = false;
        service.SettingsSaved += (_, _) =>
        {
            // 设置页读的就是 service.Settings 这一个对象。
            seenByListener = service.Settings.AnimationStyle;
            // 此刻盘上应该也已经写好了。
            fileHadIt = PickerSettings
                .Load(Path.Combine(_configFolder, "settings.json"))
                .AnimationStyle == RevealAnimationStyle.Wheel;
        };

        service.SaveSettings();

        Assert.Equal(RevealAnimationStyle.Wheel, seenByListener);
        Assert.True(fileHadIt, "广播发出时还没落盘，重启之后设置会变回去");

        // 从盘上单独再读一份，确认真的写下去了。
        var reloaded = PickerSettings.Load(Path.Combine(_configFolder, "settings.json"));
        Assert.Equal(RevealAnimationStyle.Wheel, reloaded.AnimationStyle);
    }

    /// <summary>改几次就该广播几次，不多不少。</summary>
    [Fact]
    public void SaveSettings_EachCallBroadcastsOnce()
    {
        var service = new PickerHostService(_configFolder);
        var notified = 0;
        service.SettingsSaved += (_, _) => notified++;

        service.SaveSettings();
        service.SaveSettings();
        service.SaveSettings();

        Assert.Equal(3, notified);
    }

    /// <summary>清掉临时目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configFolder))
            {
                Directory.Delete(_configFolder, true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响测试结果，忽略。
        }
    }
}
