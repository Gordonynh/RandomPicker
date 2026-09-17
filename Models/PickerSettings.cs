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
    Photo
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
/// 抽选动画样式。
/// </summary>
/// <remarks>
/// 动画只是「表演」：抽中的结果在点击的那一刻就已经定好了，
/// 所以换任何样式都不会改变抽中概率，也不会影响「本轮已抽」的记账。
/// </remarks>
public enum RevealAnimationStyle
{
    /// <summary>不播动画：抽完直接出结果（一直以来的行为，也是默认值）。</summary>
    None,

    /// <summary>中央大字原地快速换名字，线性减速，最后定格在中选者上。</summary>
    Scroll,

    /// <summary>一排名字方块横向滚过，指针停在中间那一个上面，像 CSGO 开箱。</summary>
    Csgo,

    /// <summary>若干格子逐格抽字，一个字一个字地定下来，像老虎机。</summary>
    Slot,

    /// <summary>转盘转到两个扇区的边界上，再滑进中选的那一格，像拼多多的现金转盘。</summary>
    Wheel
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

    /// <summary>悬浮窗位置（物理像素）。int.MinValue 表示还没摆过，用默认位置。</summary>
    public int WindowX { get; set; } = int.MinValue;

    /// <summary>悬浮窗位置的纵坐标（物理像素）。没摆过时同 <see cref="WindowX"/>。</summary>
    public int WindowY { get; set; } = int.MinValue;

    /// <summary>
    /// 记下 <see cref="WindowX"/>/<see cref="WindowY"/> 时，那块屏幕的宽度（物理像素）。
    /// </summary>
    /// <remarks>
    /// <b>光存坐标是不够的。</b>屏幕分辨率换过之后，同一组坐标在新屏幕上可能落到屏幕外、
    /// 或者跑到跟原来完全不同的地方去。有了这个宽度就能判断「记下位置时的屏幕还在不在」：
    /// 当<b>所有</b>屏幕的尺寸都和它不一样时，说明分辨率变过了，坐标作废，回到默认位置。
    /// <para/>
    /// 比对的是「所有屏幕里有没有一块尺寸相同的」，而不是「当前这块屏幕尺寸一样吗」——
    /// 后者在双屏下会误判：窗口摆在副屏上时，开机那一瞬间窗口还在原点，
    /// 按当前屏幕算会拿主屏的尺寸去比副屏记下的值，明明什么都没变却把位置重置了。
    /// </remarks>
    public int WindowScreenWidth { get; set; } = int.MinValue;

    /// <summary>记下位置时那块屏幕的高度（物理像素）。判定规则见 <see cref="WindowScreenWidth"/>。</summary>
    public int WindowScreenHeight { get; set; } = int.MinValue;

    /// <summary>「不重复」模式下本轮已经抽到过的人。</summary>
    public List<string> DrawnThisRound
    {
        // 直接把底层列表交出去，调用方（抽选逻辑）需要就地增删。
        get => _drawnThisRound;
        // 喵~防御：配置文件被手改成 "DrawnThisRound": null 时，反序列化会把字段整个盖成 null，
        // 之后每一次 Contains 都会崩在空引用上。这里统一兜底成空列表，
        // 语义上正好是「本轮还没抽过任何人」，是唯一安全的解释。
        set => _drawnThisRound = value ?? new List<string>();
    }

    /// <summary>本轮已抽名单的底层存储，永远不会是 null。</summary>
    private List<string> _drawnThisRound = new();

    /// <summary>上一次抽到的人，用于「随机抽选」模式回避连抽同一个。</summary>
    public string? LastPicked { get; set; }

    #region 抽选动画

    /// <summary>抽选动画样式。</summary>
    /// <remarks>
    /// <b>默认不播动画</b>，免得升级之后突然被动画到。
    /// 换成任何样式都不影响公平性——结果在点击那一刻就抽好了，动画只负责演。
    /// </remarks>
    public RevealAnimationStyle AnimationStyle { get; set; } = RevealAnimationStyle.None;

    /// <summary>「滚动名字」样式的动画时长，单位：秒。</summary>
    public double ScrollDurationSeconds { get; set; } = 4.0;

    /// <summary>「CSGO 开箱」样式的动画时长，单位：秒。</summary>
    public double CsgoDurationSeconds { get; set; } = 4.0;

    /// <summary>「老虎机」样式每一格的时长，单位：秒。总时长 = 这个值 × 格数。</summary>
    public double SlotStepSeconds { get; set; } = 1.5;

    /// <summary>「转盘」样式转到边界所用的时长，单位：秒。</summary>
    /// <remarks>滑进扇区的那一下是固定 0.35 秒，不计在这个值里面。</remarks>
    public double WheelDurationSeconds { get; set; } = 6.0;

    /// <summary>
    /// CSGO 动画里各档品质的权重，单位：百分数。
    /// </summary>
    /// <remarks>
    /// 顺序和 <c>ItemRarity</c> 的枚举一致：军规级（蓝）、受限级（紫）、保密级（粉）、
    /// 隐秘级（红）、罕见特殊物品（金）。默认就是 CS:GO 开箱那套经典分布。
    /// <para/>
    /// <b>这套权重只决定方块的颜色，不决定抽到谁。</b>「不影响实际抽取」这一点有单测钉着
    /// （见 <c>ItemRarityTests</c>：把权重全压给金，中选者依然不变）。
    /// 另外这里的默认值和 <c>ItemRarityTable</c> 里的那份必须一致，
    /// 有单测比对，防止改了一处忘了另一处。
    /// </remarks>
    public List<double> RarityWeights { get; set; } = [79.92, 15.98, 3.20, 0.64, 0.26];

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

    /// <summary>悬浮窗直径（逻辑像素）。由 <see cref="Size"/> 推导，不进配置文件。</summary>
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
