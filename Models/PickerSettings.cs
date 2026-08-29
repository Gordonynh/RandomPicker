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
