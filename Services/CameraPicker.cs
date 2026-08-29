using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ClassIsland.RandomPicker.Models;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace ClassIsland.RandomPicker.Services;

/// <summary>一个摄像头。</summary>
public sealed record CameraDevice(string Id, string Name);

/// <summary>一次拍照抽人的结果。</summary>
public sealed class ShotResult
{
    public bool Success { get; init; }

    /// <summary>失败原因，或者成功时的一句话说明。</summary>
    public string Message { get; init; } = string.Empty;

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>检出并合并之后的人脸数。</summary>
    public int FaceCount { get; init; }

    public long OpenMs { get; init; }

    public long CaptureMs { get; init; }

    public long DetectMs { get; init; }

    public long TotalMs => OpenMs + CaptureMs + DetectMs;

    /// <summary>摄像头是不是本来就热着（省掉了打开的那一两秒）。</summary>
    public bool WasWarm { get; init; }

    /// <summary>抽中那个人的人像。</summary>
    public Bitmap? Portrait { get; init; }

    /// <summary>整帧缩略图，检测框已经画上去了。设置页里用来核对。</summary>
    public Bitmap? Annotated { get; init; }

    public string Diagnostics =>
        $"画面 {FrameWidth}×{FrameHeight}，检测到 {FaceCount} 人，用时 {TotalMs / 1000.0:F1} 秒" +
        (WasWarm ? "（摄像头已开启）" : $"（其中开摄像头 {OpenMs / 1000.0:F1} 秒）");
}

/// <summary>
/// 拍一张班级合影，检测里面的人，随机挑一个裁出人像。
/// </summary>
/// <remarks>
/// <b>取帧走 <see cref="MediaFrameReader"/>，不是 <c>CapturePhotoToStreamAsync</c>。</b>
/// 实测这台机器上三个摄像头的 <c>MediaStreamType.Photo</c> 档位<b>全是空的</b>
/// （虚拟/转接摄像头只有预览流），拍照接口一律抛异常——老版本「拍不出照」就是这个原因。
/// 预览流这条路三个都能用，而且一次性建好之后<b>每帧只要 3~43 ms</b>。
/// <para/>
/// <b>摄像头保持热着。</b>打开一次要 200 ms ~ 1.5 s，首帧还要再等一会儿；
/// 抽完一次之后维持一段时间（默认 2 分钟）不关，连着抽就几乎是瞬时的。
/// 代价是这期间摄像头指示灯亮着，所以到时间会自动关掉。
/// </remarks>
internal static class CameraPicker
{
    /// <summary>首帧最多等多久。摄像头刚开时前几帧可能是空的。</summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(6);

    /// <summary>整帧缩略图的最大边长。设置页显示用，不需要原图那么大。</summary>
    private const int AnnotatedMaxSide = 900;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static MediaCapture? _capture;
    private static MediaFrameReader? _reader;
    private static string? _openDeviceId;
    private static DateTime _lastUsed = DateTime.MinValue;
    private static Timer? _idleTimer;

    private static FaceModel? _model;
    private static string? _modelError;

    /// <summary>摄像头当前是不是开着的。设置页里显示用。</summary>
    public static bool IsWarm => _capture is not null;

    /// <summary>系统里有哪些摄像头。</summary>
    public static async Task<List<CameraDevice>> ListCamerasAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.Select(x => new CameraDevice(x.Id, x.Name)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 拍一张、检测、随机挑一个裁出来。
    /// </summary>
    /// <param name="settings">插件设置。</param>
    /// <param name="pluginDirectory">插件目录，模型和 ONNX Runtime 都在这儿。</param>
    /// <param name="configFolder">要存原图时存到哪。</param>
    /// <param name="annotate">是否额外产出一张画了检测框的缩略图（设置页试拍时才要）。</param>
    public static async Task<ShotResult> CaptureAndPickAsync(PickerSettings settings,
        string pluginDirectory, string configFolder, bool annotate = false)
    {
        await Gate.WaitAsync();
        try
        {
            var clock = Stopwatch.StartNew();
            var wasWarm = _capture is not null && _openDeviceId == ResolveWantedId(settings);

            if (!await EnsureCameraAsync(settings))
            {
                return Fail("未检测到摄像头，或系统未允许访问相机");
            }

            var openMs = clock.ElapsedMilliseconds;

            clock.Restart();
            var frame = await GrabFrameAsync();
            var captureMs = clock.ElapsedMilliseconds;
            if (frame is null)
            {
                return Fail("摄像头无画面");
            }

            var (pixels, width, height) = frame.Value;
            Touch(settings);

            if (settings.SavePhotos)
            {
                await SavePhotoAsync(pixels, width, height, configFolder);
            }

            clock.Restart();
            List<FaceBox> faces;
            try
            {
                faces = Detect(pixels, width, height, settings, pluginDirectory);
            }
            catch (Exception ex)
            {
                return new ShotResult
                {
                    Success = false,
                    Message = $"检测未能启动:{ex.Message}",
                    FrameWidth = width,
                    FrameHeight = height,
                    OpenMs = openMs,
                    CaptureMs = captureMs,
                    WasWarm = wasWarm
                };
            }

            var detectMs = clock.ElapsedMilliseconds;

            var annotated = annotate ? await RenderAnnotatedAsync(pixels, width, height, faces) : null;

            if (faces.Count == 0)
            {
                return new ShotResult
                {
                    Success = false,
                    Message = "未检测到人脸",
                    FrameWidth = width,
                    FrameHeight = height,
                    FaceCount = 0,
                    OpenMs = openMs,
                    CaptureMs = captureMs,
                    DetectMs = detectMs,
                    WasWarm = wasWarm,
                    Annotated = annotated
                };
            }

            var chosen = faces[RandomNumberGenerator.GetInt32(faces.Count)];
            var portrait = await CropAsync(pixels, width, height, Expand(chosen, width, height, settings));

            return new ShotResult
            {
                Success = true,
                Message = $"从 {faces.Count} 人中抽取一人",
                FrameWidth = width,
                FrameHeight = height,
                FaceCount = faces.Count,
                OpenMs = openMs,
                CaptureMs = captureMs,
                DetectMs = detectMs,
                WasWarm = wasWarm,
                Portrait = portrait,
                Annotated = annotated
            };
        }
        catch (Exception ex)
        {
            await ShutdownCameraAsync();
            return Fail(ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ShotResult Fail(string message) => new() { Success = false, Message = message };

    #region 摄像头

    private static string? ResolveWantedId(PickerSettings settings) =>
        string.IsNullOrEmpty(settings.CameraDeviceId) ? null : settings.CameraDeviceId;

    /// <summary>摄像头没开就开，开着的就直接用。</summary>
    private static async Task<bool> EnsureCameraAsync(PickerSettings settings)
    {
        var wanted = ResolveWantedId(settings);
        if (_capture is not null && _reader is not null && _openDeviceId == wanted)
        {
            return true;
        }

        await ShutdownCameraAsync();

        var cameras = await ListCamerasAsync();
        if (cameras.Count == 0)
        {
            return false;
        }

        var device = cameras.FirstOrDefault(x => x.Id == settings.CameraDeviceId) ?? cameras[0];

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = device.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });

        var source = capture.FrameSources.Values
            .FirstOrDefault(x => x.Info.SourceKind == MediaFrameSourceKind.Color);
        if (source is null)
        {
            capture.Dispose();
            return false;
        }

        // 分辨率直接决定远处的小脸还剩几个像素，所以尽量取高的；
        // 但过了 2560 宽之后，多出来的像素在缩到 640×640 时全被扔掉，只是白白拖慢取帧。
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 2560 && f.VideoFormat.Height <= 2560)
            .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenBy(f => f.Subtype.Equals("NV12", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (format is not null)
        {
            await source.SetFormatAsync(format);
        }

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            reader.Dispose();
            capture.Dispose();
            return false;
        }

        _capture = capture;
        _reader = reader;
        _openDeviceId = wanted;
        return true;
    }

    /// <summary>抓一帧。刚开机时前几帧可能是空的，要等一等。</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)?> GrabFrameAsync()
    {
        if (_reader is null)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + FirstFrameTimeout;
        while (DateTime.UtcNow < deadline)
        {
            using var frame = _reader.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is not null)
            {
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
                bitmap.CopyToBuffer(buffer);
                bitmap.Dispose();
                return (buffer.ToArray(), width, height);
            }

            await Task.Delay(10);
        }

        return null;
    }

    /// <summary>记一次使用时间，并按设置安排空闲关闭。</summary>
    private static void Touch(PickerSettings settings)
    {
        _lastUsed = DateTime.UtcNow;

        var keepAlive = Math.Clamp(settings.CameraKeepAliveSeconds, 0, 3600);
        _idleTimer?.Dispose();
        _idleTimer = null;

        if (keepAlive <= 0)
        {
            // 不保温：用完立刻关。
            _ = Task.Run(async () =>
            {
                await Gate.WaitAsync();
                try { await ShutdownCameraAsync(); }
                finally { Gate.Release(); }
            });
            return;
        }

        var due = TimeSpan.FromSeconds(keepAlive);
        _idleTimer = new Timer(async _ =>
        {
            await Gate.WaitAsync();
            try
            {
                // 期间又用过就不关，等下一轮。
                if (DateTime.UtcNow - _lastUsed >= due - TimeSpan.FromSeconds(1))
                {
                    await ShutdownCameraAsync();
                }
            }
            catch (Exception)
            {
                // 关不掉就算了，下次再说。
            }
            finally
            {
                Gate.Release();
            }
        }, null, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>把摄像头关掉，指示灯灭掉。</summary>
    public static async Task ShutdownCameraAsync()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;

        if (_reader is not null)
        {
            try { await _reader.StopAsync(); }
            catch (Exception) { /* 已经停了 */ }

            _reader.Dispose();
            _reader = null;
        }

        _capture?.Dispose();
        _capture = null;
        _openDeviceId = null;
    }

    /// <summary>插件停止时调用：关摄像头、放掉模型。</summary>
    public static async Task DisposeAllAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await ShutdownCameraAsync();
            _model?.Dispose();
            _model = null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SavePhotoAsync(byte[] pixels, int width, int height, string configFolder)
    {
        try
        {
            var dir = Path.Combine(configFolder, "照片");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await using var file = File.Create(path);
            using var stream = await EncodePngAsync(pixels, width, height);
            await stream.AsStreamForRead().CopyToAsync(file);
        }
        catch (Exception)
        {
            // 存不上不影响抽人。
        }
    }

    #endregion

    #region 检测

    private static List<FaceBox> Detect(byte[] pixels, int width, int height,
        PickerSettings settings, string pluginDirectory)
    {
        if (_modelError is not null)
        {
            throw new InvalidOperationException(_modelError);
        }

        if (_model is null)
        {
            try
            {
                _model = new FaceModel(pluginDirectory);
            }
            catch (Exception ex)
            {
                // 记下来，之后每次都直接报同一个原因，不用反复试着加载。
                _modelError = ex.Message;
                throw;
            }
        }

        var threshold = (float)Math.Clamp(settings.FaceScoreThreshold, 0.05, 0.95);
        var found = _model.Detect(pixels, width, height, threshold);

        // 分块：把画面切成有重叠的小块各检一遍。人特别多、坐得特别远时能多找出一些，
        // 代价是每块一次推理。默认关——实测单次已经够用了。
        if (settings.UseTiledDetection)
        {
            var grid = Math.Clamp(settings.TileGrid, 2, 4);
            foreach (var tile in Tiles(width, height, grid))
            {
                var (cropped, cw, ch) = Crop(pixels, width, height, tile);
                foreach (var face in _model.Detect(cropped, cw, ch, threshold))
                {
                    found.Add(face with { X = face.X + tile.X, Y = face.Y + tile.Y });
                }
            }
        }

        return FaceModel.Merge(found);
    }

    private static IEnumerable<FaceBox> Tiles(int width, int height, int grid)
    {
        const double overlap = 0.2;
        double stepX = width / (double)grid, stepY = height / (double)grid;
        for (var row = 0; row < grid; row++)
        {
            for (var col = 0; col < grid; col++)
            {
                var x = (int)Math.Max(0, col * stepX - stepX * overlap);
                var y = (int)Math.Max(0, row * stepY - stepY * overlap);
                var right = (int)Math.Min(width, (col + 1) * stepX + stepX * overlap);
                var bottom = (int)Math.Min(height, (row + 1) * stepY + stepY * overlap);
                if (right - x > 32 && bottom - y > 32)
                {
                    yield return new FaceBox(x, y, right - x, bottom - y, 0);
                }
            }
        }
    }

    #endregion

    #region 裁切与成图

    /// <summary>把人脸框放大成「头 + 肩」的人像框，并夹回画面范围内。</summary>
    private static FaceBox Expand(FaceBox face, int width, int height, PickerSettings settings)
    {
        var targetWidth = (int)(face.Width * Math.Clamp(settings.CropWidthFactor, 1.0, 4.0));
        var targetHeight = (int)(face.Height * Math.Clamp(settings.CropHeightFactor, 1.0, 5.0));

        var centerX = face.X + face.Width / 2;
        // 纵向中心往下挪一点：人脸框基本只框住脸，多出来的高度该给肩膀而不是头顶上的空气。
        var centerY = face.Y + (int)(face.Height * 0.62);

        var x = Math.Clamp(centerX - targetWidth / 2, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(centerY - targetHeight / 2, 0, Math.Max(0, height - 1));
        return new FaceBox(x, y,
            Math.Max(1, Math.Min(targetWidth, width - x)),
            Math.Max(1, Math.Min(targetHeight, height - y)), face.Score);
    }

    private static (byte[] Pixels, int Width, int Height) Crop(byte[] pixels, int width, int height, FaceBox area)
    {
        var w = Math.Clamp(area.Width, 1, width - area.X);
        var h = Math.Clamp(area.Height, 1, height - area.Y);
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(pixels, ((area.Y + y) * width + area.X) * 4, result, y * w * 4, w * 4);
        }

        return (result, w, h);
    }

    private static async Task<Bitmap?> CropAsync(byte[] pixels, int width, int height, FaceBox area)
    {
        try
        {
            var (cropped, w, h) = Crop(pixels, width, height, area);
            return await ToAvaloniaBitmapAsync(cropped, w, h);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>整帧缩略图 + 检测框，给设置页核对用。</summary>
    private static async Task<Bitmap?> RenderAnnotatedAsync(byte[] pixels, int width, int height,
        List<FaceBox> boxes)
    {
        try
        {
            var copy = (byte[])pixels.Clone();

            void Plot(int x, int y)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    return;
                }

                var o = (y * width + x) * 4;
                copy[o] = 0x40;
                copy[o + 1] = 0xFF;
                copy[o + 2] = 0x40;
                copy[o + 3] = 0xFF;
            }

            // 线宽跟着分辨率走，不然 1080p 上一像素的框根本看不见。
            var thickness = Math.Max(1, width / 640);
            foreach (var b in boxes)
            {
                for (var t = 0; t < thickness; t++)
                {
                    for (var x = b.X; x <= b.Right; x++)
                    {
                        Plot(x, b.Y + t);
                        Plot(x, b.Bottom - t);
                    }

                    for (var y = b.Y; y <= b.Bottom; y++)
                    {
                        Plot(b.X + t, y);
                        Plot(b.Right - t, y);
                    }
                }
            }

            var scale = Math.Min(1.0, AnnotatedMaxSide / (double)Math.Max(width, height));
            using var stream = await EncodePngAsync(copy, width, height);
            var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);
            memory.Position = 0;
            return scale < 1.0
                ? Bitmap.DecodeToWidth(memory, Math.Max(1, (int)(width * scale)))
                : new Bitmap(memory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<InMemoryRandomAccessStream> EncodePngAsync(byte[] bgra, int width, int height)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    private static async Task<Bitmap?> ToAvaloniaBitmapAsync(byte[] bgra, int width, int height)
    {
        try
        {
            using var stream = await EncodePngAsync(bgra, width, height);
            var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);
            memory.Position = 0;
            return new Bitmap(memory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion
}
