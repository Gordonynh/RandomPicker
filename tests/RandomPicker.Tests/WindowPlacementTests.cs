using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 悬浮钮摆位计算的测试。
/// </summary>
/// <remarks>
/// <b>这一批是补「系统重启后悬浮钮跑到屏幕左上角」那个 bug 的。</b>
/// <para/>
/// 原因不是位置没存下来——配置读写一直是好的。而是开窗那一刻显示器信息可能还没就绪：
/// 拿到的是空矩形，或者缩放比例是个坏值（0、NaN，或者把百分比当成了倍数）。
/// 拿这种值照常算，<c>Math.Clamp</c> 的合法区间会塌成屏幕原点那一个点，
/// 窗口就被硬拽到 (0,0)。
/// <para/>
/// 这类坏值在正常使用中永远不出现，只有开机那一瞬间才有，所以必须在纯计算层把它钉死：
/// <b>算不准就返回 null（不要动窗口），绝不能返回一个「最保守的位置」。</b>
/// 位置不动只是不好看，钉死在左上角是坏掉。
/// </remarks>
public class WindowPlacementTests
{
    /// <summary>一块普通的 1920×1080 主屏。</summary>
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1080);

    /// <summary>悬浮钮直径，逻辑像素。</summary>
    private const double Diameter = 76;

    /// <summary>窗口宽度，逻辑像素。</summary>
    private const double WindowWidth = 76;

    /// <summary>窗口高度，逻辑像素。</summary>
    private const double WindowHeight = 76;

    #region 屏幕信息不可信：一律不要动窗口

    /// <summary>
    /// 屏幕矩形是空的（显示器信息还没探到）时不夹取——这正是那个 bug 的根因。
    /// </summary>
    /// <remarks>
    /// 旧写法在空矩形上会算出 <c>maxX == bounds.X == 0</c>，
    /// 于是把窗口位置夹成 (0,0)，也就是屏幕左上角。
    /// </remarks>
    [Fact]
    public void Clamp_EmptyScreenArea_ReturnsNull()
    {
        var result = WindowPlacement.Clamp((1200, 700), new ScreenArea(0, 0, 0, 0), 1.0,
            WindowWidth, WindowHeight);
        Assert.Null(result);
    }

    /// <summary>屏幕高度是 0（只探到一半）时同样不夹取。</summary>
    [Fact]
    public void Clamp_ZeroHeightScreenArea_ReturnsNull()
    {
        var result = WindowPlacement.Clamp((1200, 700), new ScreenArea(0, 0, 1920, 0), 1.0,
            WindowWidth, WindowHeight);
        Assert.Null(result);
    }

    /// <summary>
    /// 缩放比例是坏值时不夹取。
    /// </summary>
    /// <param name="scaling">坏掉的缩放比例。</param>
    /// <remarks>
    /// 缩放比例参与「窗口占多少物理像素」的换算。它一旦离谱，算出来的窗口尺寸就会
    /// 大过整块屏幕，合法区间随之塌成屏幕原点——症状和空矩形一模一样。
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    // 把百分比（150）当成了倍数。
    [InlineData(150.0)]
    public void Clamp_BadScaling_ReturnsNull(double scaling)
    {
        var result = WindowPlacement.Clamp((1200, 700), Primary, scaling, WindowWidth, WindowHeight);
        Assert.Null(result);
    }

    /// <summary>窗口比屏幕还大时不夹取——夹的话会把窗口钉死在左上角。</summary>
    [Fact]
    public void Clamp_WindowLargerThanScreen_ReturnsNull()
    {
        var result = WindowPlacement.Clamp((10, 10), Primary, 1.0, 4000, 4000);
        Assert.Null(result);
    }

    #endregion

    #region 屏幕信息可信：正常夹取

    /// <summary>
    /// 位置本来就在屏幕里时原样返回。
    /// </summary>
    [Fact]
    public void Clamp_PositionInsideScreen_KeepsIt()
    {
        var result = WindowPlacement.Clamp((1200, 700), Primary, 1.0, WindowWidth, WindowHeight);
        Assert.Equal((1200, 700), result);
    }

    /// <summary>位置超出右边界时夹回到「刚好贴着右边缘」。</summary>
    [Fact]
    public void Clamp_PastRightEdge_ClampsToRightEdge()
    {
        var result = WindowPlacement.Clamp((5000, 700), Primary, 1.0, WindowWidth, WindowHeight);
        Assert.NotNull(result);
        // 右边贴着 1920，左边就是 1920 - 76。
        Assert.Equal(1920 - 76, result.Value.X);
        // 纵坐标没超，不该被改。
        Assert.Equal(700, result.Value.Y);
    }

    /// <summary>位置超出下边界时夹回到「刚好贴着下边缘」。</summary>
    [Fact]
    public void Clamp_PastBottomEdge_ClampsToBottomEdge()
    {
        var result = WindowPlacement.Clamp((1200, 5000), Primary, 1.0, WindowWidth, WindowHeight);
        Assert.NotNull(result);
        Assert.Equal(1200, result.Value.X);
        Assert.Equal(1080 - 76, result.Value.Y);
    }

    /// <summary>坐标是负数（挪到屏幕左上方外面了）时夹回屏幕内。</summary>
    [Fact]
    public void Clamp_NegativePosition_ClampsToOrigin()
    {
        var result = WindowPlacement.Clamp((-500, -500), Primary, 1.0, WindowWidth, WindowHeight);
        Assert.Equal((0, 0), result);
    }

    /// <summary>
    /// 副屏在主屏左边时，坐标系是负的，夹取要在副屏自己的范围里做。
    /// </summary>
    /// <remarks>
    /// 多显示器下 <c>Screen.Bounds.X</c> 可能是负的。旧写法用 <c>Math.Max</c> 兜过下界，
    /// 这里换成纯计算之后同样要保证不越到副屏外面去。
    /// </remarks>
    [Fact]
    public void Clamp_SecondaryScreenOnTheLeft_UsesItsOwnBounds()
    {
        // 一块摆在主屏左边的副屏：左上角是 (-1920, 0)。
        var secondary = new ScreenArea(-1920, 0, 1920, 1080);
        // 位置在副屏范围内。
        var result = WindowPlacement.Clamp((-1000, 500), secondary, 1.0, WindowWidth, WindowHeight);
        Assert.Equal((-1000, 500), result);
        // 越到副屏左边外面时夹回副屏的左边缘（-1920），而不是主屏的 0。
        var outside = WindowPlacement.Clamp((-5000, 500), secondary, 1.0, WindowWidth, WindowHeight);
        Assert.Equal(-1920, outside?.X);
    }

    /// <summary>高分屏上窗口尺寸要按缩放比例换算成物理像素来夹。</summary>
    /// <remarks>
    /// 150% 缩放下 76 逻辑像素 = 114 物理像素。算错这一点，夹取就会偏。
    /// </remarks>
    [Fact]
    public void Clamp_ScaledScreen_ConvertsLogicalSizeToPhysical()
    {
        var result = WindowPlacement.Clamp((5000, 700), Primary, 1.5, WindowWidth, WindowHeight);
        Assert.NotNull(result);
        // 114 = ceil(76 × 1.5)。
        Assert.Equal(1920 - 114, result.Value.X);
    }

    #endregion

    #region 首次运行的默认位置

    /// <summary>
    /// 首次运行时摆在屏幕右下角靠里一点，而且整颗钮都在屏幕里。
    /// </summary>
    [Fact]
    public void DefaultCorner_PutsKnobInsideBottomRight()
    {
        var result = WindowPlacement.DefaultCorner(Primary, 1.0, Diameter);
        Assert.NotNull(result);

        var (x, y) = result.Value;
        // 整颗钮必须在屏幕里：左边不越界、右边也不越界。
        Assert.True(x >= 0 && x + Diameter <= Primary.Width, $"横向越界了：x = {x}");
        Assert.True(y >= 0 && y + Diameter <= Primary.Height, $"纵向越界了：y = {y}");
        // 而且是靠右边的，不是靠左边——这一条正好把「跑到左上角」的 bug 挡住。
        Assert.True(x > Primary.Width / 2, $"应该靠右，实际 x = {x}");
        Assert.True(y > Primary.Height / 2, $"应该靠下，实际 y = {y}");
    }

    /// <summary>高分屏上默认位置要按缩放比例换算，离边的视觉空隙才一样宽。</summary>
    [Fact]
    public void DefaultCorner_ScaledScreen_ScalesMargin()
    {
        var normal = WindowPlacement.DefaultCorner(Primary, 1.0, Diameter);
        var hidpi = WindowPlacement.DefaultCorner(Primary, 2.0, Diameter);
        Assert.NotNull(normal);
        Assert.NotNull(hidpi);

        // 200% 下钮和边距都翻倍，离右下角的物理距离自然更远，x 更小。
        Assert.True(hidpi.Value.X < normal.Value.X);
        // 但整颗钮仍然在屏幕里。
        Assert.True(hidpi.Value.X + (int)Math.Ceiling(Diameter * 2.0) <= Primary.Width);
    }

    /// <summary>工作区是空的时候不摆——交给重试。</summary>
    /// <remarks>
    /// 这里返回 null 而不是「摆到 0,0」，是刻意的：
    /// 摆错位置用户会以为插件坏了，不摆只是保持系统默认位置，重试补上就好。
    /// </remarks>
    [Fact]
    public void DefaultCorner_EmptyWorkingArea_ReturnsNull()
    {
        Assert.Null(WindowPlacement.DefaultCorner(new ScreenArea(0, 0, 0, 0), 1.0, Diameter));
    }

    /// <summary>缩放比例是坏值时不摆。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(150.0)]
    public void DefaultCorner_BadScaling_ReturnsNull(double scaling)
    {
        Assert.Null(WindowPlacement.DefaultCorner(Primary, scaling, Diameter));
    }

    #endregion

    #region 缩放比例的合理区间

    /// <summary>正常范围内的缩放比例都算可信。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(0.25)]
    [InlineData(8.0)]
    public void IsScalingSane_NormalValues_AreSane(double scaling)
    {
        Assert.True(WindowPlacement.IsScalingSane(scaling));
    }

    /// <summary>坏值都不可信。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(100.0)]
    public void IsScalingSane_BadValues_AreNotSane(double scaling)
    {
        Assert.False(WindowPlacement.IsScalingSane(scaling));
    }

    #endregion

    #region 分辨率变过就该重置位置

    /// <summary>记位置时那块屏幕还在，位置照旧用。</summary>
    [Fact]
    public void MatchesAnyScreen_WithTheSameScreen_ReturnsTrue()
    {
        Assert.True(WindowPlacement.MatchesAnyScreen(1920, 1080, [new ScreenArea(0, 0, 1920, 1080)]));
    }

    /// <summary>
    /// 分辨率换过（1920×1080 → 3840×2160）就找不到了，调用方据此回到默认位置。
    /// </summary>
    /// <remarks>
    /// 这就是主人要的「屏幕分辨率变化时重置位置」：那组坐标在新屏幕上未必还有意义，
    /// 可能落在屏幕外、也可能跑到完全不相干的地方。
    /// </remarks>
    [Fact]
    public void MatchesAnyScreen_WhenTheScreenChanged_ReturnsFalse()
    {
        Assert.False(WindowPlacement.MatchesAnyScreen(1920, 1080, [new ScreenArea(0, 0, 3840, 2160)]));
    }

    /// <summary>
    /// 双屏：主屏换成了 4K，但副屏还是原来那块——窗口本来就摆在副屏上，位置依然有效。
    /// </summary>
    /// <remarks>
    /// <b>判据是「任意一块对得上」，不是「当前这块对得上」。</b>
    /// 开机那一瞬间窗口还在原点，按当前屏幕算会拿主屏的尺寸去比副屏记下的值，
    /// 明明什么都没变，位置却被重置了。
    /// </remarks>
    [Fact]
    public void MatchesAnyScreen_WithASecondScreenOfTheOldSize_ReturnsTrue()
    {
        Assert.True(WindowPlacement.MatchesAnyScreen(1920, 1080,
            [new ScreenArea(0, 0, 3840, 2160), new ScreenArea(3840, 0, 1920, 1080)]));
    }

    /// <summary>
    /// 没记录过屏幕尺寸时一律按「没变过」处理。
    /// </summary>
    /// <remarks>
    /// 喵~防御：老版本的配置文件里只有坐标、没有尺寸。要是把「没记录」当成「找不到」，
    /// 那升级一次就会把主人摆好的位置重置掉。只记了一半（配置文件被手改坏）同样处理。
    /// </remarks>
    [Fact]
    public void MatchesAnyScreen_WithNoRecordedSize_ReturnsTrue()
    {
        ScreenArea[] screens = [new(0, 0, 3840, 2160)];

        Assert.True(WindowPlacement.MatchesAnyScreen(int.MinValue, int.MinValue, screens));
        Assert.True(WindowPlacement.MatchesAnyScreen(1920, int.MinValue, screens));
        Assert.True(WindowPlacement.MatchesAnyScreen(int.MinValue, 1080, screens));
    }

    /// <summary>
    /// 一块屏幕都拿不到（开机那一瞬间显示器信息还没就绪）时也要保留原位置。
    /// </summary>
    /// <remarks>
    /// 喵~防御：位置没摆对只是不好看，重置错了是坏掉——拿不准的时候什么都不做。
    /// </remarks>
    [Fact]
    public void MatchesAnyScreen_WithNoScreens_ReturnsTrue()
    {
        Assert.True(WindowPlacement.MatchesAnyScreen(1920, 1080, []));
        Assert.True(WindowPlacement.MatchesAnyScreen(1920, 1080, null));
    }

    #endregion
}
