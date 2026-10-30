using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// 随机抽一个小组，比如「C 组」。
    /// </summary>
    /// <remarks>
    /// 抽的是<b>组的标签</b>，不是名单里的人。组有几组、用字母还是数字，
    /// 由 <see cref="PickerSettings.GroupCount"/> 和 <see cref="PickerSettings.GroupStyle"/> 决定。
    /// </remarks>
    Group
}

/// <summary>
/// 抽组时的组名写法。
/// </summary>
public enum GroupStyle
{
    /// <summary>A 组、B 组、C 组……最多到 Z 组。</summary>
    Letter,

    /// <summary>1 组、2 组、3 组……</summary>
    Number
}

/// <summary>
/// 悬浮球尺寸档位。
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
    /// <summary>
    /// 抽什么。等同于「抽取来源」：名单（<see cref="PickMode.Random"/> /
    /// <see cref="PickMode.NoRepeat"/>）、抽组（<see cref="PickMode.Group"/>）、拍照（<see cref="PickMode.Photo"/>）。
    /// </summary>
    public PickMode Mode { get; set; } = PickMode.NoRepeat;

    /// <summary>
    /// 放回 / 不放回。<see cref="PickMode.NoRepeat"/> = 不放回，<see cref="PickMode.Random"/> = 放回。
    /// </summary>
    /// <remarks>
    /// 名单和抽组共用这一个设定：
    /// <list type="bullet">
    /// <item>「随机抽人」时它就是 <see cref="Mode"/> 本身；</item>
    /// <item>「随机抽组」时 <see cref="Mode"/> 是 <see cref="PickMode.Group"/>，这个值决定组要不要放回；</item>
    /// <item>拍照没有放回可言，但这个值<b>照样记着</b>，切回来时按它恢复。</item>
    /// </list>
    /// 判断「当前是不是不放回」一律走 <see cref="IsNoRepeat"/>，别自己拿 <see cref="Mode"/> 比。
    /// </remarks>
    public PickMode TextMode { get; set; } = PickMode.NoRepeat;

    /// <summary>当前是不是「不放回」。名单、抽组、拍照三种来源统一看这个。</summary>
    [JsonIgnore]
    public bool IsNoRepeat => Mode switch
    {
        PickMode.NoRepeat => true,
        PickMode.Group => TextMode == PickMode.NoRepeat,
        _ => false
    };

    /// <summary>
    /// 一次抽几个。菜单和设置页给的都是 1 / 2 / 3。
    /// </summary>
    /// <remarks>
    /// 三种来源都吃它：名单抽 N 个<b>互不重复</b>的人，拍照从同一张合影里挑 N 张不同的人脸，
    /// 抽组抽 N 个不同的组。实际能抽到多少会被名单长度 / 检出的人脸数 / 小组总数卡住，
    /// 抽不出那么多就少给几个。
    /// </remarks>
    public int PickCount { get; set; } = 1;

    #region 抽组

    /// <summary>小组总数的上限。26 是字母写法的天花板（A ~ Z）。</summary>
    public const int MaxGroups = 26;

    /// <summary>组名用字母还是数字。</summary>
    public GroupStyle GroupStyle { get; set; } = GroupStyle.Letter;

    /// <summary>
    /// 一共分几个组。1 ~ 26——上限 26 是因为字母写法只到 Z。
    /// </summary>
    public int GroupCount { get; set; } = 6;

    /// <summary>「不放回」模式下本轮已经抽到过的组。</summary>
    public List<string> DrawnGroupsThisRound { get; set; } = new();

    /// <summary>上一次抽到的组。不放回模式开新一轮时用它避开「连着两次抽到同一组」。</summary>
    public string? LastGroupPicked { get; set; }

    /// <summary>
    /// 最近抽到的几个组，<b>最近的排最前</b>。放回模式下按它降权。
    /// </summary>
    /// <remarks>长度由 <c>WeightedPicker.Memory</c> 卡着，不用无限增长。</remarks>
    public List<string> RecentGroups { get; set; } = new();

    #endregion

    /// <summary>悬浮球大小档位。</summary>
    public PickerSize Size { get; set; } = PickerSize.Medium;

    /// <summary>
    /// 悬浮球上那一行小字是否显示人数。
    /// </summary>
    /// <remarks>
    /// 关掉之后改成显示「放回 / 不放回」——有人觉得桌面上那个数字一直在变很碍眼，
    /// 只想知道当前是哪种抽法。
    /// <para/>
    /// 拍照模式下那一行本来就写「拍照」，不受这个开关影响。
    /// </remarks>
    public bool ShowPickerCount { get; set; } = true;

    /// <summary>
    /// 悬浮窗是否被用户藏起来了。
    /// </summary>
    /// <remarks>
    /// 藏起来只是把窗口 <c>Hide()</c>，实例仍然留着，随时能叫回来。
    /// 这个状态<b>会存盘</b>：重启 ClassIsland 之后仍然保持隐藏，不用每次开机再点一次。
    /// 叫回来的路有两条：托盘菜单「更多选项 → 显示抽选悬浮球」，或者应用设置里的这一页。
    /// </remarks>
    public bool Hidden { get; set; }

    /// <summary>是否在屏幕中央弹出结果（名字、组名或多张人像）。</summary>
    public bool ShowCenterReveal { get; set; } = true;

    /// <summary>是否同时调用 ClassIsland 的提醒弹窗。</summary>
    public bool ShowNotification { get; set; } = true;

    /// <summary>中央弹窗停留秒数。</summary>
    public double RevealSeconds { get; set; } = 2.5;

    /// <summary>悬浮球位置（物理像素）。<c>int.MinValue</c> 表示还没摆过，用默认位置。</summary>
    public int WindowX { get; set; } = int.MinValue;

    /// <summary>与 <see cref="WindowX"/> 成对。</summary>
    public int WindowY { get; set; } = int.MinValue;

    /// <summary>
    /// 当前用第几份名单。1 ~ 5，对应配置目录下 <c>namelib/list1.txt</c> ~ <c>list5.txt</c>。
    /// </summary>
    /// <remarks>
    /// 只记序号，不记名字：名字写在名单文件里（<c>@高一5班</c>），
    /// 改名不该让「上次用的是哪份」错位。
    /// </remarks>
    public int SelectedList { get; set; } = 1;

    /// <summary>「不重复」模式下本轮已经抽到过的人。</summary>
    public List<string> DrawnThisRound { get; set; } = new();

    /// <summary>
    /// 最近抽到的几个人，<b>最近的排最前</b>。放回模式下按它降权。
    /// </summary>
    /// <remarks>长度由 <c>WeightedPicker.Memory</c> 卡着，不用无限增长。</remarks>
    public List<string> RecentPicks { get; set; } = new();

    /// <summary>
    /// 上一次抽到的人。不放回模式开新一轮时用它避开「连着两次抽到同一个人」。
    /// </summary>
    public string? LastPicked { get; set; }

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

    #region 模式切换

    /// <summary>
    /// 切换抽人来源：拍照（从合影里挑人脸）、随机抽人（从名单里抽）、随机抽组。
    /// </summary>
    /// <param name="source">
    /// <see cref="PickMode.Photo"/> / <see cref="PickMode.Group"/> 直接切过去；
    /// 传别的（比如 <see cref="TextMode"/>）表示切回「随机抽人」。
    /// </param>
    /// <remarks>
    /// 切回「随机抽人」时用 <see cref="TextMode"/> 恢复，不然用户当初选的「不放回」会被忘掉。
    /// 悬浮球右键菜单和设置页都调这里，免得两边各写一套、慢慢走样。
    /// </remarks>
    public void SetSource(PickMode source)
    {
        if (source is PickMode.Photo or PickMode.Group)
        {
            Mode = source;
            return;
        }

        Mode = TextMode is PickMode.Random or PickMode.NoRepeat ? TextMode : PickMode.NoRepeat;
    }

    /// <summary>
    /// 设置放回 / 不放回。名单和抽组共用。
    /// </summary>
    /// <remarks>
    /// 当前是名单或抽组时立刻生效；拍照模式下只是先记下来
    /// （拍照没有放回可言），切回来时自动按它恢复。
    /// </remarks>
    public void SetReplace(PickMode mode)
    {
        if (mode is not (PickMode.Random or PickMode.NoRepeat))
        {
            return;
        }

        TextMode = mode;

        // 只有当前正好是「名单」来源时才改 Mode——抽组时 Mode 得留在 Group，
        // 拍照时更要原样不动。
        if (Mode is PickMode.Random or PickMode.NoRepeat)
        {
            Mode = mode;
        }
    }

    /// <summary>
    /// 「重置本轮」。两种模式都叫这个名字，做的事不一样。
    /// </summary>
    /// <remarks>
    /// <b>不放回</b>：就是字面意思——清掉本轮已抽的人和组，下一抽重新从头开始。
    /// <para/>
    /// <b>放回</b>：没有轮次可言，这里重置的是<b>权重</b>。放回靠「最近抽过的降权」来避免
    /// 同一节课里有人被抽三次、有人一次没抽，抽久了那份记忆会让某些人「怎么都抽不到」，
    /// 清掉它，所有人回到同一权重。
    /// <para/>
    /// 逻辑放在模型里而不是宿主里，是为了能单独跑测试——
    /// 悬浮球右键菜单和设置页最终都走这一个入口，不会两边不一致。
    /// </remarks>
    public void ResetRound()
    {
        if (IsNoRepeat)
        {
            DrawnThisRound.Clear();
            DrawnGroupsThisRound.Clear();
            return;
        }

        RecentPicks.Clear();
        RecentGroups.Clear();

        // 「上一个是哪个」也一起清：它只在开新一轮时用来避开连抽，留着没有意义，
        // 反而会让下一抽莫名其妙地漏掉一个人（或一个组）。
        LastPicked = null;
        LastGroupPicked = null;
    }

    #endregion

    #region 读写

    /// <summary>
    /// 用另一份设置覆盖自己，<b>实例不变</b>。
    /// </summary>
    /// <remarks>
    /// 「刷新插件状态」要从磁盘重读 <c>settings.json</c>，但悬浮球和设置页都攥着同一个实例，
    /// 换成新对象等于把它们手里的引用作废。所以搬值，不换实例。
    /// <para/>
    /// 列表属性要<b>新拷一份</b>，不能直接赋值：那是从 <c>Load</c> 出来的临时对象，
    /// 把它的 List 挂到长期存活的实例上，改起来会牵扯不清。
    /// <para/>
    /// <b>以后新增设置项记得在这儿补一行</b>——漏了不会报错，只会让刷新看起来「没生效」。
    /// </remarks>
    public void CopyFrom(PickerSettings other)
    {
        Mode = other.Mode;
        TextMode = other.TextMode;
        PickCount = other.PickCount;

        GroupStyle = other.GroupStyle;
        GroupCount = other.GroupCount;
        DrawnGroupsThisRound = new List<string>(other.DrawnGroupsThisRound);
        LastGroupPicked = other.LastGroupPicked;
        RecentGroups = new List<string>(other.RecentGroups);

        Size = other.Size;
        ShowPickerCount = other.ShowPickerCount;
        Hidden = other.Hidden;
        ShowCenterReveal = other.ShowCenterReveal;
        ShowNotification = other.ShowNotification;
        RevealSeconds = other.RevealSeconds;
        WindowX = other.WindowX;
        WindowY = other.WindowY;

        SelectedList = other.SelectedList;
        DrawnThisRound = new List<string>(other.DrawnThisRound);
        RecentPicks = new List<string>(other.RecentPicks);
        LastPicked = other.LastPicked;

        CameraDeviceId = other.CameraDeviceId;
        CameraDeviceName = other.CameraDeviceName;
        CameraKeepAliveSeconds = other.CameraKeepAliveSeconds;
        FaceScoreThreshold = other.FaceScoreThreshold;
        UseTiledDetection = other.UseTiledDetection;
        TileGrid = other.TileGrid;
        PhotoAvoidRecent = other.PhotoAvoidRecent;
        PhotoTextChance = other.PhotoTextChance;
        SeparateTextRoster = other.SeparateTextRoster;
        CropWidthFactor = other.CropWidthFactor;
        CropHeightFactor = other.CropHeightFactor;
        SavePhotos = other.SavePhotos;
    }

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
                return Normalize(JsonSerializer.Deserialize<PickerSettings>(
                    File.ReadAllText(path), JsonOptions) ?? new PickerSettings());
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值重来，不要因为一个 json 拦住整个插件。
        }

        return new PickerSettings();
    }

    /// <summary>
    /// 把读进来的设置收拾干净。
    /// </summary>
    /// <remarks>
    /// 老配置里没有 <see cref="TextMode"/> 和 <see cref="PickCount"/>，反序列化出来是默认值，
    /// 会和 <see cref="Mode"/> 对不上——这里按约定补回去，老用户升级后不用重新选一遍模式。
    /// </remarks>
    private static PickerSettings Normalize(PickerSettings s)
    {
        // 老配置里没有 TextMode，也可能是脏值。TextMode 只允许「放回 / 不放回」两个值，
        // 而名单来源下它必须和 Mode 一致。
        var textModeValid = s.TextMode is PickMode.Random or PickMode.NoRepeat;
        var modeIsText = s.Mode is PickMode.Random or PickMode.NoRepeat;

        if (!textModeValid || (modeIsText && s.TextMode != s.Mode))
        {
            s.TextMode = modeIsText ? s.Mode : PickMode.NoRepeat;
        }

        s.PickCount = Math.Clamp(s.PickCount, 1, 3);
        s.GroupCount = Math.Clamp(s.GroupCount, 1, MaxGroups);

        // 名单份数是 NameListLibrary.SlotCount，这里不引用它是为了不让模型层依赖服务层。
        s.SelectedList = Math.Clamp(s.SelectedList, 1, 5);

        // 老配置只有「上一次抽到谁」，没有最近几次的记录。补一条进去，
        // 升级后的第一次抽仍然会避开刚抽过的那个。
        if (s.RecentPicks.Count == 0 && !string.IsNullOrEmpty(s.LastPicked))
        {
            s.RecentPicks.Add(s.LastPicked);
        }

        if (s.RecentGroups.Count == 0 && !string.IsNullOrEmpty(s.LastGroupPicked))
        {
            s.RecentGroups.Add(s.LastGroupPicked);
        }

        return s;
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

    /// <summary>悬浮球直径（逻辑像素）。由 <see cref="Size"/> 推导，不进配置文件。</summary>
    [JsonIgnore]
    public double Diameter => Size switch
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
