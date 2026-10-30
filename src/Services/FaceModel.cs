using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClassIsland.RandomPicker.Services;

/// <summary>检测到的一张人脸（原始画面的像素坐标）。</summary>
public sealed record FaceBox(int X, int Y, int Width, int Height, float Score)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;
}

/// <summary>
/// YuNet 人脸检测模型。
/// </summary>
/// <remarks>
/// 用的是 OpenCV Zoo 的 <c>face_detection_yunet_2023mar.onnx</c>（232 KB），
/// 靠 ONNX Runtime 跑。两样都随插件一起打包——宿主没有。
/// <para/>
/// <b>为什么不用 Windows 自带的 <c>FaceDetector</c>：</b>拿一张 1320×1214 的看台照片实测，
/// 自带的整帧只检出 4 张脸，切成 4×4 放大再检也只有 58 张、要 736 ms；
/// YuNet 单次推理就是 <b>97 张 / 63 ms</b>。快一个数量级，还多找出七成的人。
/// <para/>
/// 几个来之不易的事实，改这段之前先读：
/// <list type="bullet">
/// <item>这个 onnx 的输入维度是<b>写死的 640×640</b>，喂别的尺寸会被 ORT 直接拒绝。</item>
/// <item>摄像头画面大多是 16:9，直接拉成正方会掉召回。实测同一张 16:9 画面，
///       <b>补边保持比例 67 张、拉伸只有 60 张</b>，所以这里走补边。</item>
/// <item>OpenCV 默认的 0.9 分数阈值在人多的场景下只能检出 1 张，完全不能用。默认取 0.5。</item>
/// </list>
/// </remarks>
internal sealed class FaceModel : IDisposable
{
    /// <summary>模型要求的输入边长。写死的，不能改。</summary>
    public const int InputSize = 640;

    /// <summary>补边用的中性灰，和常见的 letterbox 实现保持一致。</summary>
    private const byte PadValue = 114;

    private static readonly int[] Strides = [8, 16, 32];

    private static bool _nativeHooked;
    private static readonly object HookGate = new();

    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>输入张量只建一次，每次检测复用，省掉每帧几 MB 的分配。</summary>
    private readonly DenseTensor<float> _input = new(new[] { 1, 3, InputSize, InputSize });

    private readonly NamedOnnxValue[] _inputs;

    public FaceModel(string pluginDirectory)
    {
        EnsureNativeResolvable(pluginDirectory);

        var modelPath = Path.Combine(pluginDirectory, "face_detection_yunet_2023mar.onnx");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"找不到人脸检测模型：{modelPath}");
        }

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            // 教室电脑通常核心不多，全占满反而抢了 UI 线程。留一半，最多 4 个。
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _inputs = [NamedOnnxValue.CreateFromTensor(_inputName, _input)];
    }

    /// <summary>
    /// 在一帧 BGRA 画面里找人脸。
    /// </summary>
    /// <param name="bgra">BGRA8 像素，长度必须是 <paramref name="width"/> × <paramref name="height"/> × 4。</param>
    /// <param name="scoreThreshold">分数阈值。越低找得越多，也越容易把花纹认成脸。</param>
    public List<FaceBox> Detect(byte[] bgra, int width, int height, float scoreThreshold)
    {
        // 补边：等比缩到能塞进 640×640，剩下的地方填中性灰。
        var zoom = Math.Min(InputSize / (double)width, InputSize / (double)height);
        var fitW = Math.Max(1, (int)Math.Round(width * zoom));
        var fitH = Math.Max(1, (int)Math.Round(height * zoom));
        var padX = (InputSize - fitW) / 2;
        var padY = (InputSize - fitH) / 2;

        FillInput(bgra, width, height, fitW, fitH, padX, padY);

        using var results = _session.Run(_inputs);
        return Decode(results, scoreThreshold, padX, padY, zoom);
    }

    /// <summary>
    /// 双线性缩放 + 补边 + 转成 BGR planar，一趟走完。
    /// </summary>
    /// <remarks>
    /// 不走 WIC 的 <c>BitmapTransform</c>：那条路要先把画面编码再解码，
    /// 光这一下就比整个推理还慢。直接在像素数组上采样，几毫秒就够了。
    /// <para/>
    /// YuNet 吃的是 <b>0-255 的原始值，不做归一化</b>，通道顺序 BGR。
    /// </remarks>
    private void FillInput(byte[] bgra, int srcW, int srcH, int fitW, int fitH, int padX, int padY)
    {
        var span = _input.Buffer.Span;
        const int plane = InputSize * InputSize;

        span.Fill(PadValue);

        var xRatio = srcW / (double)fitW;
        var yRatio = srcH / (double)fitH;

        for (var y = 0; y < fitH; y++)
        {
            // 取源像素中心，避免整体偏移半个像素。
            var fy = (y + 0.5) * yRatio - 0.5;
            var y0 = (int)Math.Floor(fy);
            var wy = fy - y0;
            y0 = Math.Clamp(y0, 0, srcH - 1);
            var y1 = Math.Min(y0 + 1, srcH - 1);

            var dstRow = (y + padY) * InputSize + padX;
            var srcRow0 = y0 * srcW;
            var srcRow1 = y1 * srcW;

            for (var x = 0; x < fitW; x++)
            {
                var fx = (x + 0.5) * xRatio - 0.5;
                var x0 = (int)Math.Floor(fx);
                var wx = fx - x0;
                x0 = Math.Clamp(x0, 0, srcW - 1);
                var x1 = Math.Min(x0 + 1, srcW - 1);

                var a = (srcRow0 + x0) * 4;
                var b = (srcRow0 + x1) * 4;
                var c = (srcRow1 + x0) * 4;
                var d = (srcRow1 + x1) * 4;

                var i = dstRow + x;
                for (var ch = 0; ch < 3; ch++)
                {
                    var top = bgra[a + ch] + (bgra[b + ch] - bgra[a + ch]) * wx;
                    var bottom = bgra[c + ch] + (bgra[d + ch] - bgra[c + ch]) * wx;
                    span[ch * plane + i] = (float)(top + (bottom - top) * wy);
                }
            }
        }
    }

    /// <summary>
    /// 把三个尺度的输出解成人脸框。
    /// </summary>
    /// <remarks>
    /// 和 OpenCV <c>FaceDetectorYN</c> 一致：
    /// <c>cx = (列 + bbox0) × stride</c>、<c>cy = (行 + bbox1) × stride</c>、
    /// <c>w = exp(bbox2) × stride</c>、<c>h = exp(bbox3) × stride</c>、
    /// <c>分数 = sqrt(cls × obj)</c>。先验点按<b>行优先</b>排列，
    /// 每个尺度的数量正好是 <c>(640/stride)²</c>。
    /// </remarks>
    private static List<FaceBox> Decode(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        float threshold, int padX, int padY, double zoom)
    {
        var map = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());
        var boxes = new List<FaceBox>();

        foreach (var stride in Strides)
        {
            if (!map.TryGetValue($"cls_{stride}", out var cls) ||
                !map.TryGetValue($"obj_{stride}", out var obj) ||
                !map.TryGetValue($"bbox_{stride}", out var bbox))
            {
                continue;
            }

            var side = InputSize / stride;
            for (var row = 0; row < side; row++)
            {
                for (var col = 0; col < side; col++)
                {
                    var i = row * side + col;
                    var score = MathF.Sqrt(
                        Math.Clamp(cls.GetValue(i), 0f, 1f) * Math.Clamp(obj.GetValue(i), 0f, 1f));
                    if (score < threshold)
                    {
                        continue;
                    }

                    var b = i * 4;
                    var cx = (col + bbox.GetValue(b)) * stride;
                    var cy = (row + bbox.GetValue(b + 1)) * stride;
                    var bw = MathF.Exp(bbox.GetValue(b + 2)) * stride;
                    var bh = MathF.Exp(bbox.GetValue(b + 3)) * stride;

                    // 去掉补边偏移，再按缩放系数还原到原始画面坐标。
                    boxes.Add(new FaceBox(
                        (int)((cx - bw / 2 - padX) / zoom),
                        (int)((cy - bh / 2 - padY) / zoom),
                        (int)(bw / zoom),
                        (int)(bh / zoom),
                        score));
                }
            }
        }

        return boxes;
    }

    /// <summary>按重叠度合并重复框，面积大、分数高的优先留下。</summary>
    public static List<FaceBox> Merge(List<FaceBox> boxes, double iouThreshold = 0.3)
    {
        var kept = new List<FaceBox>();
        foreach (var box in boxes.OrderByDescending(x => x.Score).ThenByDescending(x => x.Area))
        {
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            var duplicate = false;
            foreach (var other in kept)
            {
                var x = Math.Max(other.X, box.X);
                var y = Math.Max(other.Y, box.Y);
                var right = Math.Min(other.Right, box.Right);
                var bottom = Math.Min(other.Bottom, box.Bottom);
                if (right <= x || bottom <= y)
                {
                    continue;
                }

                double overlap = (double)(right - x) * (bottom - y);
                if (overlap / (other.Area + box.Area - overlap) > iouThreshold)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    /// <summary>
    /// 让 <c>Microsoft.ML.OnnxRuntime</c> 找得到插件目录里的 <c>onnxruntime.dll</c>。
    /// </summary>
    /// <remarks>
    /// 插件不生成 deps.json（生成了宿主的 <c>AssemblyDependencyResolver</c> 会照着它
    /// 去插件目录找所有依赖，把本该用宿主那份的 Avalonia 之类也一起拖下水），
    /// 而原生库的默认解析恰恰依赖 deps.json。所以这里直接按绝对路径挂一个解析器。
    /// </remarks>
    private static void EnsureNativeResolvable(string pluginDirectory)
    {
        lock (HookGate)
        {
            if (_nativeHooked)
            {
                return;
            }

            _nativeHooked = true;
            var ortAssembly = typeof(InferenceSession).Assembly;
            NativeLibrary.SetDllImportResolver(ortAssembly, (name, _, _) =>
            {
                if (!name.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                var path = Path.Combine(pluginDirectory, "onnxruntime.dll");
                return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
            });
        }
    }

    /// <summary>
    /// 让插件自带的 <c>Microsoft.ML.OnnxRuntime.dll</c> 能被加载。
    /// </summary>
    /// <remarks>
    /// 由插件入口在最开始调用一次。宿主里没有这个程序集，而插件没有 deps.json，
    /// 所以要在<b>插件自己的</b> PluginLoadContext 上挂解析回调，按路径加载。
    /// </remarks>
    public static void EnsureManagedResolvable(Assembly pluginAssembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(pluginAssembly);
        if (context is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(pluginAssembly.Location);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        context.Resolving += (ctx, requested) =>
        {
            if (requested.Name is null)
            {
                return null;
            }

            var candidate = Path.Combine(directory, requested.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
    }

    public void Dispose() => _session.Dispose();
}
