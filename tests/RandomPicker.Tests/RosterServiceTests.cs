using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 名单读取与抽选的测试。
/// </summary>
/// <remarks>
/// 每个测试实例独享一个临时目录，测完就删。名单服务会真的读写文件、还挂着文件监视，
/// 共用目录的话前一个测试留下的名单会被后一个测试读到。
/// </remarks>
public sealed class RosterServiceTests : IDisposable
{
    /// <summary>本测试独享的临时目录。</summary>
    private readonly string _temporaryDirectory;

    /// <summary>临时目录里的名单文件路径。</summary>
    private readonly string _rosterPath;

    public RosterServiceTests()
    {
        // 目录名带随机后缀，避免并行跑测试时两个实例撞在同一个目录上。
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "RandomPickerTests", Guid.NewGuid().ToString("N"));
        // 先把目录建出来，后面写名单文件要用。
        Directory.CreateDirectory(_temporaryDirectory);
        // 文件名和插件正式使用的一致，出问题时一眼能对上。
        _rosterPath = Path.Combine(_temporaryDirectory, "名单.txt");
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
            // 喵~防御：文件监视器或别的进程可能还短暂占着目录。
            // 临时目录残留不影响测试结论，不值得把整个测试判成失败。
        }
    }

    /// <summary>写一份名单文件，一行一个名字。</summary>
    private void WriteRoster(params string[] lines) =>
        // 用不带 BOM 的 UTF-8，和插件自己生成示例名单时的编码保持一致。
        File.WriteAllLines(_rosterPath, lines, new UTF8Encoding(false));

    /// <summary>造一个被测的名单服务。</summary>
    private RosterService CreateRoster() => new(_rosterPath);

    #region 读取与解析

    [Fact]
    public void FirstRun_CreatesSampleRosterWithThreeNames()
    {
        // 首次运行：文件还不存在，服务应当自己生成一份带说明的示例。
        using var roster = CreateRoster();

        // 文件确实被创建出来了。
        Assert.True(File.Exists(_rosterPath));
        // 示例里的注释行不算名字，只留下三个人。
        Assert.Equal(["张三", "李四", "王五"], roster.Names);
    }

    [Fact]
    public void Parsing_IgnoresCommentLinesAndBlankLines()
    {
        // 混入注释行、空行、以及只有空白的行。
        WriteRoster("# 这是注释", "", "   ", "张三", "#李四", "王五");

        using var roster = CreateRoster();

        // 「#李四」整行以 # 开头，按规则算注释，所以只剩张三和王五两个人。
        Assert.Equal(["张三", "王五"], roster.Names);
    }

    [Fact]
    public void Parsing_TrimsSurroundingWhitespace()
    {
        // 名字前后有多余空格和制表符。
        WriteRoster("  张三  ", "\t李四");

        using var roster = CreateRoster();

        // 空格不该被当成名字的一部分，否则「张三」和「 张三 」会被算成两个人。
        Assert.Equal(["张三", "李四"], roster.Names);
    }

    [Fact]
    public void Parsing_TreatsHashInTheMiddleAsNormalCharacter()
    {
        // # 只在行首才是注释，出现在中间就是名字的一部分。
        WriteRoster("张#三");

        using var roster = CreateRoster();

        Assert.Equal(["张#三"], roster.Names);
    }

    [Fact]
    public void Parsing_DuplicateNamesCountOnce()
    {
        // 同一个名字写了两遍。
        WriteRoster("张三", "李四", "张三");

        using var roster = CreateRoster();

        // 重复的只算一个，否则那个人被抽中的概率会翻倍。
        Assert.Equal(["张三", "李四"], roster.Names);
    }

    [Fact]
    public void MissingRosterFile_ReloadsToEmptyAndPickReturnsNull()
    {
        // 先正常建一份名单。
        WriteRoster("张三", "李四");
        using var roster = CreateRoster();

        // 用户把文件删了。
        File.Delete(_rosterPath);
        // 手动重新载入。
        roster.Reload();

        // 名单变空。
        Assert.Empty(roster.Names);
        // 空名单抽不出人，返回 null 而不是抛异常。
        Assert.Null(roster.Pick(new PickerSettings()));
    }

    [Fact]
    public void Reload_WhenFileIsLockedByAnotherProcess_KeepsPreviousRoster()
    {
        // 先正常建一份名单并读进来。
        WriteRoster("张三", "李四");
        using var roster = CreateRoster();

        // 独占打开文件，模拟「用户正在记事本里保存」的那一瞬间。
        using (var locked = new FileStream(_rosterPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 先确认这个锁真的生效了：此刻从别处读会失败。
            Assert.Throws<IOException>(() => File.ReadAllText(_rosterPath));

            // 读取失败的情况下重载，服务应当自己吞掉这个异常。
            roster.Reload();
        }

        // 名单保持上一次的内容，没有被清空。
        Assert.Equal(["张三", "李四"], roster.Names);
    }

    #endregion

    #region 抽选

    [Fact]
    public void EmptyRoster_PickReturnsNull()
    {
        // 空文件：一个人都没有。
        WriteRoster();
        using var roster = CreateRoster();

        var settings = new PickerSettings();

        // 抽不到人时返回 null，由界面去显示「名单是空的」。
        Assert.Null(roster.Pick(settings));
        // 也不该往「本轮已抽」里塞任何东西。
        Assert.Empty(settings.DrawnThisRound);
    }

    [Fact]
    public void SingleName_IsAlwaysReturnedAndRemembered()
    {
        // 名单里只有一个人。
        WriteRoster("张三");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.Random };

        // 只有一个人时直接返回他，不走随机。
        Assert.Equal("张三", roster.Pick(settings));
        // 上一次抽到谁也要记下来，界面和回避逻辑都依赖这个字段。
        Assert.Equal("张三", settings.LastPicked);
    }

    [Fact]
    public void NoRepeatMode_CoversEveryoneExactlyOncePerRound()
    {
        // 五个人，抽满一轮应当每人一次。
        WriteRoster("张三", "李四", "王五", "赵六", "钱七");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.NoRepeat };

        // 记下这一轮抽到的顺序。
        var drawn = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            // 名单非空，这里一定抽得到人，感叹号是安全的。
            drawn.Add(roster.Pick(settings)!);
        }

        // 五个人各不相同，也就是每个人都恰好出现一次。
        Assert.Equal(5, drawn.Distinct().Count());
        // 「本轮已抽」也攒满了五个人。
        Assert.Equal(5, settings.DrawnThisRound.Count);
        // 本轮剩余人数归零。
        Assert.Equal(0, roster.RemainingInRound(settings));
    }

    [Fact]
    public void NoRepeatMode_AfterFullRound_StartsNewRoundWithoutRepeatingLastPicked()
    {
        // 三个人，方便精确算轮次。
        WriteRoster("张三", "李四", "王五");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.NoRepeat };

        // 第一轮：抽满三次。
        var firstRound = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            firstRound.Add(roster.Pick(settings)!);
        }

        // 第一轮的三个人互不相同。
        Assert.Equal(3, firstRound.Distinct().Count());

        // 已经抽完一轮，再抽一次应当自动开新一轮。
        var next = roster.Pick(settings)!;

        // 新一轮的第一抽不能是上一轮最后抽到的那个人，
        // 否则用户看到的就是「连着两次抽到同一个」，像是坏了。
        Assert.NotEqual(firstRound[^1], next);
        // 新一轮的已抽名单里目前只有这一个人。
        Assert.Equal([next], settings.DrawnThisRound);
    }

    [Fact]
    public void RandomMode_NeverPicksSamePersonTwiceInARow()
    {
        // 三个人，纯随机模式。
        WriteRoster("张三", "李四", "王五");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.Random };

        // 先抽一次作为基准。
        var previous = roster.Pick(settings);

        // 连抽 200 次，任何相邻两次都不该是同一个人。
        // 这个断言是确定性的：候选池里已经把上一个人剔掉了，和随机数好坏无关。
        for (var i = 0; i < 200; i++)
        {
            var current = roster.Pick(settings);
            Assert.NotEqual(previous, current);
            previous = current;
        }
    }

    [Fact]
    public void RandomMode_DoesNotTrackDrawnThisRound()
    {
        // 纯随机模式没有轮次的概念。
        WriteRoster("张三", "李四");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.Random };

        // 多抽几次。
        for (var i = 0; i < 20; i++)
        {
            roster.Pick(settings);
        }

        // 「本轮已抽」应当一直是空的，否则切回不重复模式时会莫名其妙少几个人。
        Assert.Empty(settings.DrawnThisRound);
    }

    [Fact]
    public void RemainingInRound_InRandomMode_EqualsTotalCount()
    {
        // 三个人。
        WriteRoster("张三", "李四", "王五");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.Random };

        // 抽两次。
        roster.Pick(settings);
        roster.Pick(settings);

        // 纯随机模式没有轮次概念，剩余人数就是总人数。
        Assert.Equal(3, roster.RemainingInRound(settings));
    }

    [Fact]
    public void RemainingInRound_InNoRepeatMode_DecreasesAfterEachPick()
    {
        // 四个人。
        WriteRoster("张三", "李四", "王五", "赵六");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.NoRepeat };

        // 一开始一个都没抽，剩四个。
        Assert.Equal(4, roster.RemainingInRound(settings));

        // 抽一个，剩三个。
        roster.Pick(settings);
        Assert.Equal(3, roster.RemainingInRound(settings));
    }

    [Fact]
    public void ResetRound_ClearsDrawnList()
    {
        // 三个人。
        WriteRoster("张三", "李四", "王五");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.NoRepeat };

        // 抽两个人。
        roster.Pick(settings);
        roster.Pick(settings);
        Assert.Equal(2, settings.DrawnThisRound.Count);

        // 用户点了菜单里的「开始新一轮」。
        RosterService.ResetRound(settings);

        // 已抽名单清空，剩余人数回到总数。
        Assert.Empty(settings.DrawnThisRound);
        Assert.Equal(3, roster.RemainingInRound(settings));
    }

    [Fact]
    public void LastPicked_IsUpdatedOnEveryPick()
    {
        // 两个人。
        WriteRoster("张三", "李四");
        using var roster = CreateRoster();
        var settings = new PickerSettings { Mode = PickMode.Random };

        // 抽一次，记下是谁。
        var first = roster.Pick(settings);
        Assert.Equal(first, settings.LastPicked);

        // 再抽一次，上一次抽到谁要跟着更新。
        var second = roster.Pick(settings);
        Assert.Equal(second, settings.LastPicked);
        // 顺带确认两次不是同一个人（随机模式的回避规则）。
        Assert.NotEqual(first, second);
    }

    #endregion

    #region 文件变化

    [Fact]
    public void FileWatcher_ReloadsAfterFileIsRewritten()
    {
        // 先有一份两个人的名单。
        WriteRoster("张三", "李四");
        using var roster = CreateRoster();
        Assert.Equal(2, roster.Names.Count);

        // 用户改成三个人并保存。
        WriteRoster("张三", "李四", "王五");

        // 文件监视是异步的，而且「先清空再写」的保存方式会连着触发好几次事件，
        // 所以这里轮询等一小会儿，而不是睡死一个固定时长。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && roster.Names.Count < 3)
        {
            Thread.Sleep(50);
        }

        // 名单被自动重新载入，不需要重启插件。
        Assert.Equal(["张三", "李四", "王五"], roster.Names);
    }

    [Fact]
    public void RosterChanged_IsRaisedAfterReload()
    {
        // 先有一份名单。
        WriteRoster("张三");
        using var roster = CreateRoster();

        // 挂上变更通知，用一个信号量等它。
        using var changed = new SemaphoreSlim(0);
        roster.RosterChanged += (_, _) => changed.Release();

        // 改名单。
        WriteRoster("张三", "李四");

        // 最多等 5 秒，收到通知就算过。
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)), "名单变化后应当触发 RosterChanged 事件");
    }

    #endregion

    #region 空参数防御

    [Fact]
    public void Pick_WithNullSettings_Throws()
    {
        // 名单本身是正常的，问题只出在传入的参数上。
        WriteRoster("张三");
        using var roster = CreateRoster();

        // 传 null 应当立刻抛出带参数名的异常，而不是在后面某一行冒出空引用异常。
        Assert.Throws<ArgumentNullException>(() => roster.Pick(null!));
    }

    [Fact]
    public void RemainingInRound_WithNullSettings_Throws()
    {
        WriteRoster("张三");
        using var roster = CreateRoster();

        // 同样要求早报错。
        Assert.Throws<ArgumentNullException>(() => roster.RemainingInRound(null!));
    }

    [Fact]
    public void ResetRound_WithNullSettings_Throws()
    {
        // 静态方法也要挡住 null。
        Assert.Throws<ArgumentNullException>(() => RosterService.ResetRound(null!));
    }

    #endregion
}
