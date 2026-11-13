using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.Shared.Enums;

namespace ClassIsland.RandomPicker.Models;

/// <summary>
/// 抽选模式。
/// </summary>
public enum PickMode
{
    /// <summary>每次都从全部名单里抽，只回避「和上一次同一个人」。</summary>
    Random,

    /// <summary>抽到的人本轮不再出现，抽完一轮自动重新开始。</summary>
    NoRepeat,

    /// <summary>
    /// 用摄像头拍一张班级合影，从检测到的人里随机挑一个，裁出他的人像。
    /// </summary>
    /// <remarks>
    /// 和名单无关——抽的是照片里的人，不是名字。检测不到人脸时会退回按名单抽。
    /// </remarks>
    Photo,

    /// <summary>
    /// 把当前名单按每组人数切成若干组，抽中一组后显示组名和组员。
    /// </summary>
    /// <remarks>小组有自己独立的轮次记录，和按人抽的「本轮已抽」互不干扰。</remarks>
    Group
}

/// <summary>
/// 悬浮窗尺寸档位。
/// </summary>
public enum PickerSize
{
    Small,
    Medium,
    Large
}

/// <summary>
/// 插件设置。存在插件配置目录下的 <c>settings.json</c>。
/// </summary>
public class PickerSettings
{
    public PickMode Mode { get; set; } = PickMode.NoRepeat;

    public PickerSize Size { get; set; } = PickerSize.Medium;

    /// <summary>是否在屏幕中央弹出大字。</summary>
    public bool ShowCenterReveal { get; set; } = true;

    /// <summary>是否同时调用 ClassIsland 的提醒弹窗。</summary>
    public bool ShowNotification { get; set; } = true;

    /// <summary>中央大字停留秒数。</summary>
    public double RevealSeconds { get; set; } = 2.5;

    /// <summary>悬浮窗位置（物理像素）。NaN 表示还没摆过，用默认位置。</summary>
    public int WindowX { get; set; } = int.MinValue;

    public int WindowY { get; set; } = int.MinValue;

    /// <summary>「不重复」模式下本轮已经抽到过的人。</summary>
    public List<string> DrawnThisRound { get; set; } = new();

    /// <summary>上一次抽到的人，用于「随机抽选」模式回避连抽同一个。</summary>
    public string? LastPicked { get; set; }

    #region 多份名单 / 小组抽选

    /// <summary>
    /// 当前用第几份名单（1~5）。1 对应原来的 <c>名单.txt</c>，老配置升级后行为不变。
    /// </summary>
    public int ActiveRosterIndex { get; set; } = 1;

    /// <summary>小组抽选时每组几个人。</summary>
    public int GroupSize { get; set; } = 4;

    /// <summary>小组抽选本轮已经抽到过的组号。和按人抽的 <see cref="DrawnThisRound"/> 分开记。</summary>
    public List<int> DrawnGroupsThisRound { get; set; } = new();

    /// <summary>上一次抽到的组号，用于新一轮第一抽回避连抽同一组。</summary>
    public int? LastGroupIndex { get; set; }

    #endregion

    #region 悬浮窗 / 权重 / 外部调用

    /// <summary>
    /// 悬浮窗是否被用户藏起来了。
    /// </summary>
    /// <remarks>
    /// 隐藏时除了 <c>Hide()</c>，还必须挂起置顶定时器；恢复走设置页的「显示悬浮窗」开关，
    /// 不再需要重启 ClassIsland。
    /// </remarks>
    public bool HideFloatingWindow { get; set; }

    /// <summary>是否启用名单权重。关掉之后所有名字等概率，名单里写的权重也不生效。</summary>
    public bool EnableNameWeights { get; set; } = true;

    /// <summary>
    /// 每个名字的权重覆盖值。设置页里改的就是这份，优先级高于名单文件里写的 <c>*N</c>。
    /// </summary>
    public Dictionary<string, double> NameWeights { get; set; } = new(StringComparer.Ordinal);

    /// <summary>URL / 自动化动作没指定人数时，一次抽多少人。</summary>
    public int DefaultMultiCount { get; set; } = 3;

    /// <summary>是否允许通过 <c>classisland://plugins/randompicker/...</c> 触发抽选。</summary>
    public bool EnableUriPick { get; set; } = true;

    /// <summary>
    /// 是否开启本地 HTTP 接口。<b>默认关</b>，且只监听 127.0.0.1。
    /// </summary>
    public bool EnableHttpApi { get; set; }

    /// <summary>本地 HTTP 接口端口。</summary>
    public int HttpApiPort { get; set; } = 17890;

    /// <summary>本地 HTTP 接口的令牌。留空表示不校验；填了之后请求要带 <c>token</c> 参数或 <c>X-Picker-Token</c> 头。</summary>
    public string HttpApiToken { get; set; } = string.Empty;

    /// <summary>是否启用全局快捷键切换悬浮窗显隐。默认开。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>快捷键修饰键（Win32 MOD_* 位掩码）。默认 Ctrl+Alt = 3。</summary>
    public int HotkeyModifiers { get; set; } = 3;

    /// <summary>快捷键虚拟键码（Win32 VK_*）。默认 'R' = 0x52。</summary>
    public int HotkeyVirtualKey { get; set; } = 0x52;

    /// <summary>
    /// 课间禁用抽选。开启后课间（<see cref="TimeState.Breaking"/>）里
    /// 悬浮窗、URL、HTTP、自动化行动等所有入口都不响应。
    /// </summary>
    public bool DisableDuringBreak { get; set; } = true;

    /// <summary>
    /// 打断上一次点名。开启时新抽选立刻关掉上一条还没播完的中央大字并开始新的点名；
    /// 关闭时上一次点名播完之前，新的抽选请求会被忽略。
    /// </summary>
    public bool InterruptPreviousPick { get; set; } = true;

    /// <summary>
    /// 悬浮窗缩放系数。1 = 原始大小，可调 0.5 ~ 2.0，乘在尺寸档位的直径上。
    /// </summary>
    public double FloatingWindowScale { get; set; } = 1.0;

    #endregion

    #region 拍照抽人

    /// <summary>用哪个摄像头。空 = 用系统默认的第一个。</summary>
    public string? CameraDeviceId { get; set; }

    /// <summary>上次选中的摄像头名字，只为在设置里显示得好认一点。</summary>
    public string? CameraDeviceName { get; set; }

    /// <summary>
    /// 抽完一次之后，摄像头继续开着多少秒。
    /// </summary>
    /// <remarks>
    /// 打开摄像头要 200 ms ~ 1.5 s，首帧还要再等一会儿；开着的时候每帧只要几十毫秒。
    /// 所以抽完不马上关，连着抽就几乎是瞬时的。代价是这期间摄像头指示灯亮着，
    /// 到点会自动关掉。设成 0 表示用完立刻关。
    /// </remarks>
    public int CameraKeepAliveSeconds { get; set; } = 120;

    /// <summary>
    /// 人脸分数阈值。越低找得越多，也越容易把花纹认成脸。
    /// </summary>
    /// <remarks>
    /// 拿一张看台照片实测：0.5 检出 97 人，0.7 检出 60 人，
    /// 而 OpenCV 默认的 0.9 只有 1 人——那个默认值在人多的场景下完全不能用。
    /// </remarks>
    public double FaceScoreThreshold { get; set; } = 0.5;

    /// <summary>
    /// 分块检测。
    /// </summary>
    /// <remarks>
    /// 把画面切成有重叠的小块各检一遍再合并。人特别多、坐得特别远时能多找出一些
    /// （实测 97 → 132），代价是每块一次推理。<b>默认关</b>——单次推理已经够用，
    /// 而且只要 60 ms 左右。
    /// </remarks>
    public bool UseTiledDetection { get; set; }

    /// <summary>分块的边数（3 = 切成 3×3）。</summary>
    public int TileGrid { get; set; } = 3;

    /// <summary>
    /// 拍照抽人时回避最近抽过的几个人。
    /// </summary>
    /// <remarks>
    /// 拍照模式原来每一张都是从当场检出的人脸里独立随机挑一个，<b>没有任何记忆</b>——
    /// 于是「连着两次抽到同一个人」是必然会发生的，而且在人不多的时候相当频繁：
    /// 30 个人里连抽两次撞上的概率就有 1/30，一节课抽十几次几乎一定会遇到。
    /// <para/>
    /// 教室里人不会乱动，所以用<b>人脸在画面里的位置</b>当身份：
    /// 位置落在最近抽过的那几个人附近的，这一轮先不抽。
    /// 设成 0 就是完全独立随机（原来的行为）。
    /// </remarks>
    public int PhotoAvoidRecent { get; set; } = 6;

    /// <summary>
    /// 拍照模式下有多大概率改成按名单抽文字。
    /// </summary>
    /// <remarks>
    /// 一直是照片会腻，偶尔蹦一个名字更有意思，也照顾到没被拍进画面的人。
    /// 0 = 永远拍照，100 = 永远抽名字。
    /// </remarks>
    public int PhotoTextChance { get; set; }

    /// <summary>
    /// 文字抽选用<b>单独一份名单</b>。
    /// </summary>
    /// <remarks>
    /// 开着的时候读 <c>名单-文字.txt</c>，和拍照那套完全隔离：
    /// 想让文字抽选只覆盖某几个人（比如轮到发言的小组）时不必动主名单。
    /// </remarks>
    public bool SeparateTextRoster { get; set; }

    /// <summary>裁切时相对人脸框的横向放大倍数。</summary>
    public double CropWidthFactor { get; set; } = 1.8;

    /// <summary>裁切时相对人脸框的纵向放大倍数。留出头顶和肩膀。</summary>
    public double CropHeightFactor { get; set; } = 2.4;

    /// <summary>
    /// 把拍到的原图存到插件配置目录。
    /// </summary>
    /// <remarks>
    /// <b>默认关。</b>教室合影是学生的影像，没有必要就不落盘——
    /// 不开的时候整张照片只在内存里待到裁完就丢。
    /// </remarks>
    public bool SavePhotos { get; set; }

    #endregion

    #region 权重

    /// <summary>
    /// 取某个名字最终生效的权重。设置页的覆盖值优先，其次是名单文件里写的 <c>*N</c>，
    /// 都没有就是 1。关掉总开关时一律返回 1。
    /// </summary>
    public double GetWeight(string name, double inlineWeight = 1.0)
    {
        if (!EnableNameWeights)
        {
            return 1.0;
        }

        if (NameWeights.TryGetValue(name, out var overrideWeight) &&
            double.IsFinite(overrideWeight) && overrideWeight > 0)
        {
            return overrideWeight;
        }

        return double.IsFinite(inlineWeight) && inlineWeight > 0 ? inlineWeight : 1.0;
    }

    /// <summary>设置某个名字的权重覆盖值；传 null 或非正数表示删掉覆盖，回落到名单里写的权重。</summary>
    public void SetWeight(string name, double? weight)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (weight is null || !double.IsFinite(weight.Value) || weight.Value <= 0)
        {
            NameWeights.Remove(name);
            return;
        }

        NameWeights[name] = Math.Round(Math.Clamp(weight.Value, 0.01, 999), 2);
    }

    /// <summary>清掉所有权重覆盖。</summary>
    public void ClearWeights() => NameWeights.Clear();

    /// <summary>删掉名单里已经不存在的名字，免得配置越攒越大。</summary>
    public int PruneWeights(IEnumerable<string> validNames)
    {
        var valid = new HashSet<string>(validNames, StringComparer.Ordinal);
        var stale = NameWeights.Keys.Where(x => !valid.Contains(x)).ToList();
        foreach (var name in stale)
        {
            NameWeights.Remove(name);
        }

        return stale.Count;
    }

    #endregion

    #region 读写

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PickerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<PickerSettings>(File.ReadAllText(path), JsonOptions)
                       ?? new PickerSettings();
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值重来，不要因为一个 json 拦住整个插件。
        }

        return new PickerSettings();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 存不上就算了，下次再存。
        }
    }

    #endregion

    /// <summary>悬浮窗直径（逻辑像素）。尺寸档位推导出基准值，再乘缩放系数，不进配置文件。</summary>
    [JsonIgnore]
    public double Diameter => Math.Clamp(
        Math.Round(BaseDiameter * (double.IsFinite(FloatingWindowScale) ? FloatingWindowScale : 1.0)),
        28, 320);

    /// <summary>尺寸档位对应的基准直径，不乘缩放系数。</summary>
    [JsonIgnore]
    public double BaseDiameter => Size switch
    {
        PickerSize.Small => 56,
        PickerSize.Large => 104,
        _ => 76
    };

    /// <summary>中央大字的字号（逻辑像素）。由 <see cref="Size"/> 推导，不进配置文件。</summary>
    [JsonIgnore]
    public double RevealFontSize => Size switch
    {
        PickerSize.Small => 96,
        PickerSize.Large => 200,
        _ => 148
    };

    /// <summary>人像弹窗的高度（逻辑像素）。由 <see cref="Size"/> 推导。</summary>
    [JsonIgnore]
    public double PortraitHeight => Size switch
    {
        PickerSize.Small => 320,
        PickerSize.Large => 680,
        _ => 480
    };
}
