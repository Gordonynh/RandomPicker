using System;
using System.IO;
using System.Text;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 插件设置读写与换算的测试。
/// </summary>
/// <remarks>
/// 设置是直接落到磁盘上的 json，配置被写坏、被手改、路径不对都属于要兜住的常见情况，
/// 所以这里除了正常往返，重点测「读到坏数据时会不会把插件拦住」。
/// </remarks>
public sealed class PickerSettingsTests : IDisposable
{
    /// <summary>本测试独享的临时目录。</summary>
    private readonly string _temporaryDirectory;

    /// <summary>临时目录里的设置文件路径。</summary>
    private readonly string _settingsPath;

    public PickerSettingsTests()
    {
        // 随机目录名，避免并行测试互相覆盖配置。
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "RandomPickerTests", Guid.NewGuid().ToString("N"));
        // 建目录，后面写设置文件要用。
        Directory.CreateDirectory(_temporaryDirectory);
        // 文件名与插件实际使用的一致。
        _settingsPath = Path.Combine(_temporaryDirectory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            // 递归删掉临时目录。
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 喵~防御：删不掉就留着，临时目录不影响测试结论。
        }
    }

    #region 读取

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        // 路径上什么都没有，应当安安静静给一份默认设置。
        var settings = PickerSettings.Load(_settingsPath);

        // 默认模式是「本轮内不重复」。
        Assert.Equal(PickMode.NoRepeat, settings.Mode);
        // 默认尺寸是「中」。
        Assert.Equal(PickerSize.Medium, settings.Size);
        // 默认会弹中央大字。
        Assert.True(settings.ShowCenterReveal);
        // 默认也会发 ClassIsland 提醒。
        Assert.True(settings.ShowNotification);
        // 默认不存原始照片（涉及学生影像，必须先问过用户）。
        Assert.False(settings.SavePhotos);
        // 默认回避最近 6 个人。
        Assert.Equal(6, settings.PhotoAvoidRecent);
    }

    [Fact]
    public void Load_WhenJsonIsBroken_ReturnsDefaults()
    {
        // 写一段坏掉的 json，模拟配置文件被编辑坏了。
        File.WriteAllText(_settingsPath, "{ 这不是合法的 JSON ", new UTF8Encoding(false));

        // 不能因为一个坏掉的 json 就把整个插件拦下来，应当退回默认值。
        var settings = PickerSettings.Load(_settingsPath);

        Assert.Equal(PickMode.NoRepeat, settings.Mode);
    }

    [Fact]
    public void Load_WhenJsonIsEmptyObject_KeepsDefaults()
    {
        // 合法的空对象：所有字段都缺省。
        File.WriteAllText(_settingsPath, "{}", new UTF8Encoding(false));

        var settings = PickerSettings.Load(_settingsPath);

        // 缺的字段由类里的初始值补上。
        Assert.Equal(PickerModeDefault, settings.Mode);
        Assert.Equal(76, settings.Diameter);
    }

    [Fact]
    public void Load_WhenDrawnThisRoundIsNull_FallsBackToEmptyList()
    {
        // 手改配置时很容易写成 null，反序列化会把字段整个盖成 null。
        File.WriteAllText(_settingsPath,
            "{ \"Mode\": \"NoRepeat\", \"DrawnThisRound\": null }", new UTF8Encoding(false));

        var settings = PickerSettings.Load(_settingsPath);

        // 兜底成空列表，否则之后每一次 Contains 都会崩在空引用上。
        Assert.NotNull(settings.DrawnThisRound);
        Assert.Empty(settings.DrawnThisRound);
    }

    #endregion

    #region 保存

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        // 造一份改过的设置。
        var original = new PickerSettings
        {
            Mode = PickMode.Photo,
            Size = PickerSize.Large,
            ShowCenterReveal = false,
            ShowNotification = false,
            RevealSeconds = 4.0,
            CameraDeviceId = "camera-1",
            CameraDeviceName = "教室里那个",
            CameraKeepAliveSeconds = 30,
            FaceScoreThreshold = 0.7,
            UseTiledDetection = true,
            TileGrid = 4,
            PhotoAvoidRecent = 12,
            PhotoTextChance = 25,
            SeparateTextRoster = true,
            CropWidthFactor = 2.2,
            CropHeightFactor = 3.1,
            SavePhotos = true,
            LastPicked = "张三"
        };
        // 本轮已抽的人也要一起存下来。
        original.DrawnThisRound.Add("李四");

        // 存盘再读回来。
        original.Save(_settingsPath);
        var loaded = PickerSettings.Load(_settingsPath);

        // 逐个字段核对，一个都不能丢。
        Assert.Equal(original.Mode, loaded.Mode);
        Assert.Equal(original.Size, loaded.Size);
        Assert.Equal(original.ShowCenterReveal, loaded.ShowCenterReveal);
        Assert.Equal(original.ShowNotification, loaded.ShowNotification);
        Assert.Equal(original.RevealSeconds, loaded.RevealSeconds);
        Assert.Equal(original.CameraDeviceId, loaded.CameraDeviceId);
        Assert.Equal(original.CameraDeviceName, loaded.CameraDeviceName);
        Assert.Equal(original.CameraKeepAliveSeconds, loaded.CameraKeepAliveSeconds);
        Assert.Equal(original.FaceScoreThreshold, loaded.FaceScoreThreshold);
        Assert.Equal(original.UseTiledDetection, loaded.UseTiledDetection);
        Assert.Equal(original.TileGrid, loaded.TileGrid);
        Assert.Equal(original.PhotoAvoidRecent, loaded.PhotoAvoidRecent);
        Assert.Equal(original.PhotoTextChance, loaded.PhotoTextChance);
        Assert.Equal(original.SeparateTextRoster, loaded.SeparateTextRoster);
        Assert.Equal(original.CropWidthFactor, loaded.CropWidthFactor);
        Assert.Equal(original.CropHeightFactor, loaded.CropHeightFactor);
        Assert.Equal(original.SavePhotos, loaded.SavePhotos);
        Assert.Equal(original.LastPicked, loaded.LastPicked);
        Assert.Equal(original.DrawnThisRound, loaded.DrawnThisRound);
    }

    [Fact]
    public void Save_WritesEnumAsReadableText()
    {
        // 存一份设置。
        var settings = new PickerSettings { Mode = PickMode.Photo, Size = PickerSize.Small };
        settings.Save(_settingsPath);

        // 读原始文本出来核对：枚举要写成人能看懂的名字，
        // 而不是数字——配置文件是要给用户手改的。
        var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
        Assert.Contains("Photo", json, StringComparison.Ordinal);
        Assert.Contains("Small", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        // 指向一层还不存在的子目录。
        var nestedPath = Path.Combine(_temporaryDirectory, "还没建的目录", "settings.json");

        new PickerSettings().Save(nestedPath);

        // 目录被自动建出来，文件也写成功了。
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Save_WhenPathIsUnwritable_DoesNotThrow()
    {
        // 把一个目录当成文件路径来写，必然失败。
        // 存不上不该影响用户正在做的事，最多下次再存。
        var exception = Record.Exception(() => new PickerSettings().Save(_temporaryDirectory));

        Assert.Null(exception);
    }

    #endregion

    #region 抽选动画

    [Fact]
    public void Defaults_AnimationStyleIsNone()
    {
        // 默认不播动画：升级上来的用户不该被突然动画到。
        Assert.Equal(RevealAnimationStyle.None, new PickerSettings().AnimationStyle);
    }

    [Fact]
    public void Defaults_AnimationDurationsMatchSpec()
    {
        // 四个默认时长：滚动名字 4 秒、CSGO 4 秒、老虎机每格 1.5 秒、转盘 6 秒。
        var settings = new PickerSettings();

        Assert.Equal(4.0, settings.ScrollDurationSeconds);
        Assert.Equal(4.0, settings.CsgoDurationSeconds);
        Assert.Equal(1.5, settings.SlotStepSeconds);
        Assert.Equal(6.0, settings.WheelDurationSeconds);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAnimationSettings()
    {
        // 改一遍所有动画设置，存盘再读回来，一个都不能丢。
        var original = new PickerSettings
        {
            AnimationStyle = RevealAnimationStyle.Wheel,
            ScrollDurationSeconds = 2.5,
            CsgoDurationSeconds = 3.5,
            SlotStepSeconds = 2.0,
            WheelDurationSeconds = 8.0
        };

        original.Save(_settingsPath);
        var loaded = PickerSettings.Load(_settingsPath);

        Assert.Equal(RevealAnimationStyle.Wheel, loaded.AnimationStyle);
        Assert.Equal(2.5, loaded.ScrollDurationSeconds);
        Assert.Equal(3.5, loaded.CsgoDurationSeconds);
        Assert.Equal(2.0, loaded.SlotStepSeconds);
        Assert.Equal(8.0, loaded.WheelDurationSeconds);
    }

    [Fact]
    public void Save_WritesAnimationStyleAsReadableText()
    {
        // 枚举要写成人能看懂的名字而不是数字，配置文件才是给人手改的。
        new PickerSettings { AnimationStyle = RevealAnimationStyle.Slot }.Save(_settingsPath);

        var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
        Assert.Contains("Slot", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithUnknownAnimationStyle_FallsBackToDefaults()
    {
        // 配置里写了一个不存在的样式名时，不能因为一个字段把整个插件拦住。
        File.WriteAllText(_settingsPath,
            "{ \"AnimationStyle\": \"不存在的样式\" }", new UTF8Encoding(false));

        var settings = PickerSettings.Load(_settingsPath);

        // 整份配置退回默认值（这是 Load 既有的容错行为）。
        Assert.Equal(RevealAnimationStyle.None, settings.AnimationStyle);
    }

    #endregion

    #region 由尺寸档位推导的显示参数

    [Fact]
    public void Diameter_MatchesEachSizeStep()
    {
        // 小档 56 逻辑像素。
        Assert.Equal(56, new PickerSettings { Size = PickerSize.Small }.Diameter);
        // 中档 76 逻辑像素（也是默认值）。
        Assert.Equal(76, new PickerSettings { Size = PickerSize.Medium }.Diameter);
        // 大档 104 逻辑像素。
        Assert.Equal(104, new PickerSettings { Size = PickerSize.Large }.Diameter);
    }

    [Fact]
    public void RevealFontSize_MatchesEachSizeStep()
    {
        // 字号要比圆钮大得多：它是给整间教室看的。
        Assert.Equal(96, new PickerSettings { Size = PickerSize.Small }.RevealFontSize);
        Assert.Equal(148, new PickerSettings { Size = PickerSize.Medium }.RevealFontSize);
        Assert.Equal(200, new PickerSettings { Size = PickerSize.Large }.RevealFontSize);
    }

    [Fact]
    public void PortraitHeight_MatchesEachSizeStep()
    {
        // 人像弹窗的高度也按档位走。
        Assert.Equal(320, new PickerSettings { Size = PickerSize.Small }.PortraitHeight);
        Assert.Equal(480, new PickerSettings { Size = PickerSize.Medium }.PortraitHeight);
        Assert.Equal(680, new PickerSettings { Size = PickerSize.Large }.PortraitHeight);
    }

    [Fact]
    public void DerivedProperties_AreNotWrittenToJson()
    {
        // 推导出来的属性是 [JsonIgnore] 的，不该出现在配置文件里。
        new PickerSettings().Save(_settingsPath);

        // 配置文件里不该出现这两个字段名，否则用户改了也不会生效，徒增困惑。
        var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
        Assert.DoesNotContain("Diameter", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RevealFontSize", json, StringComparison.Ordinal);
    }

    #endregion

    /// <summary>默认模式的别名，让上面断言读起来短一点。</summary>
    private const PickMode PickerModeDefault = PickMode.NoRepeat;
}
