using System;
using System.Collections.Generic;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// letterbox（等比缩放 + 居中补边）参数。
/// </summary>
/// <param name="FitWidth">等比缩放后贴进输入方块的宽度，单位：像素。</param>
/// <param name="FitHeight">等比缩放后贴进输入方块的高度，单位：像素。</param>
/// <param name="PadX">左侧补边宽度，单位：像素。</param>
/// <param name="PadY">上侧补边高度，单位：像素。</param>
/// <param name="Zoom">缩放倍数，用来把模型输出的坐标还原回原始画面。</param>
internal readonly record struct Letterbox(int FitWidth, int FitHeight, int PadX, int PadY, double Zoom);

/// <summary>
/// 人脸框的纯几何计算：letterbox 缩放、放大裁切、分块切分、距离判定。
/// </summary>
/// <remarks>
/// 从 <see cref="CameraPicker"/> 里抽出来的一层。这里的方法只跟坐标和像素数组打交道，
/// 不碰摄像头、不碰界面、不依赖任何全局状态，所以可以离线单元测试。
/// 真正需要硬件的那一半（开摄像头、取帧、编码成图）留在 <see cref="CameraPicker"/> 里。
/// </remarks>
internal static class FaceGeometry
{
    /// <summary>
    /// 算出「等比缩放 + 居中补边」塞进正方形输入所需的尺寸和偏移。
    /// </summary>
    /// <param name="sourceWidth">原始画面宽度，单位：像素。</param>
    /// <param name="sourceHeight">原始画面高度，单位：像素。</param>
    /// <param name="inputSize">模型要求的正方形边长，单位：像素。</param>
    /// <returns>缩放后的贴图尺寸、补边量，以及缩放倍数。</returns>
    /// <remarks>
    /// 人脸模型（YuNet）的输入是写死的正方形，而摄像头画面大多是 16:9。
    /// 直接拉成正方形会掉召回——同一张 16:9 画面实测：补边保住 67 张脸、拉伸只剩 60 张。
    /// 所以这里只提供等比缩放这一条路，不留拉伸选项。
    /// </remarks>
    public static Letterbox ComputeLetterbox(int sourceWidth, int sourceHeight, int inputSize)
    {
        // 喵~防御：源尺寸或目标边长只要有一个不是正数，下面的除法就会算出 Infinity 或 NaN，
        // 接着的取整会变成毫无意义的巨大值，最终在写像素时越界崩溃。
        // 这里退回到「1×1、不补边」这个最小的合法结果，让调用方拿到一张合法的空图。
        if (sourceWidth <= 0 || sourceHeight <= 0 || inputSize <= 0)
        {
            return new Letterbox(1, 1, 0, 0, 1.0);
        }

        // 横竖两个方向取更小的那个缩放倍数：保证长边刚好塞满、短边留白，画面内容一点不被切掉。
        var zoom = Math.Min(inputSize / (double)sourceWidth, inputSize / (double)sourceHeight);
        // 缩放后贴图的实际宽度，至少 1 像素，免得极端长条比例下被算成 0。
        var fitWidth = Math.Max(1, (int)Math.Round(sourceWidth * zoom));
        // 缩放后贴图的实际高度，同样至少 1 像素。
        var fitHeight = Math.Max(1, (int)Math.Round(sourceHeight * zoom));
        // 左右补边量各取一半；整数除法向下取整，多出来的那 1 像素留到右侧。
        var padX = (inputSize - fitWidth) / 2;
        // 上下补边量各取一半，多出来的那 1 像素留到下侧。
        var padY = (inputSize - fitHeight) / 2;

        // 把四项结果打包返回，调用方拿去填像素、以及把检出的坐标还原回原图。
        return new Letterbox(fitWidth, fitHeight, padX, padY, zoom);
    }

    /// <summary>
    /// 把人脸框放大成「头 + 肩」的人像框，并夹回画面范围内。
    /// </summary>
    /// <param name="face">模型检出的人脸框，坐标系是原始画面。</param>
    /// <param name="width">原始画面宽度，单位：像素。</param>
    /// <param name="height">原始画面高度，单位：像素。</param>
    /// <param name="settings">插件设置，提供横向/纵向放大倍数。</param>
    /// <returns>裁切用人像框，保证完全落在画面内且宽高至少 1 像素。</returns>
    public static FaceBox Expand(FaceBox face, int width, int height, PickerSettings settings)
    {
        // 横向放大倍数夹在 1~4 之间：小于 1 会把脸本身切掉，大于 4 会拍进半张课桌。
        var targetWidth = (int)(face.Width * Math.Clamp(settings.CropWidthFactor, 1.0, 4.0));
        // 纵向放大倍数夹在 1~5 之间：纵向要比横向留得多，才装得下头顶和肩膀。
        var targetHeight = (int)(face.Height * Math.Clamp(settings.CropHeightFactor, 1.0, 5.0));

        // 人脸框的水平中心：放大之后仍以它为基准，人像才不会偏心。
        var centerX = face.X + face.Width / 2;
        // 纵向中心特意往下挪到 62% 的位置：人脸框基本只框住脸，
        // 多出来的高度应该给肩膀，而不是头顶上的空气。
        var centerY = face.Y + (int)(face.Height * 0.62);

        // 左上角横坐标先夹回画面内；上界用 width-1，保证右边至少还剩 1 像素可裁。
        var x = Math.Clamp(centerX - targetWidth / 2, 0, Math.Max(0, width - 1));
        // 左上角纵坐标同理，保证下边至少还剩 1 像素。
        var y = Math.Clamp(centerY - targetHeight / 2, 0, Math.Max(0, height - 1));
        // 宽高还要按「右边/下边不许越界」再收一次，并兜底成至少 1 像素，否则后续裁切会拿到空区域。
        return new FaceBox(x, y,
            Math.Max(1, Math.Min(targetWidth, width - x)),
            Math.Max(1, Math.Min(targetHeight, height - y)), face.Score);
    }

    /// <summary>
    /// 从整帧 BGRA 像素里抠出一块矩形。
    /// </summary>
    /// <param name="pixels">整帧 BGRA8 像素，长度应为 width × height × 4。</param>
    /// <param name="width">整帧宽度，单位：像素。</param>
    /// <param name="height">整帧高度，单位：像素。</param>
    /// <param name="area">要抠的区域，坐标系是原始画面。</param>
    /// <returns>抠出来的像素、以及它的宽高。</returns>
    public static (byte[] Pixels, int Width, int Height) Crop(byte[] pixels, int width, int height, FaceBox area)
    {
        // 喵~防御：像素数组为 null、帧尺寸非正、或数组长度根本装不下这一帧时，
        // 后面任何一次按行拷贝都会越界抛异常。这里直接退回一张 1×1 的透明像素，
        // 由上层走「裁不出人像」的分支，而不是让整次抽人崩掉。
        if (pixels is null || width <= 0 || height <= 0 || pixels.Length < (long)width * height * 4)
        {
            return (new byte[4], 1, 1);
        }

        // 喵~防御：区域起点落在画面外时，width - area.X 会变成 0 或负数，
        // 直接丢给 Math.Clamp 会因为「下界大于上界」抛 ArgumentException。
        // 所以先把起点夹回画面内，再算真正可用的宽高。
        var startX = Math.Clamp(area.X, 0, width - 1);
        // 纵坐标同理夹回画面内。
        var startY = Math.Clamp(area.Y, 0, height - 1);
        // 可裁宽度：不超出右边界，且至少 1 像素。
        var cropWidth = Math.Clamp(area.Width, 1, width - startX);
        // 可裁高度：不超出下边界，且至少 1 像素。
        var cropHeight = Math.Clamp(area.Height, 1, height - startY);

        // 结果缓冲区：每个像素 4 字节，顺序是 B、G、R、A。
        var result = new byte[cropWidth * cropHeight * 4];

        // 逐行整段拷贝。按行拷比逐像素拷快得多，一张 1080p 的图也就几毫秒。
        for (var row = 0; row < cropHeight; row++)
        {
            // 源图这一行的起点换算成字节下标：跳过上面 row 行，再跳过左边 startX 个像素。
            var sourceOffset = ((startY + row) * width + startX) * 4;
            // 目标图这一行的起点字节下标。
            var targetOffset = row * cropWidth * 4;
            // 整行搬过去，长度就是这一行的字节数。
            Array.Copy(pixels, sourceOffset, result, targetOffset, cropWidth * 4);
        }

        // 返回抠好的像素以及它的尺寸，上层据此生成人像图。
        return (result, cropWidth, cropHeight);
    }

    /// <summary>
    /// 把画面切成网格状、彼此有重叠的小块。
    /// </summary>
    /// <param name="width">画面宽度，单位：像素。</param>
    /// <param name="height">画面高度，单位：像素。</param>
    /// <param name="grid">每边的块数（3 表示切成 3×3）。</param>
    /// <returns>一块一块的区域，顺序是先行后列。</returns>
    /// <remarks>
    /// 分块的用途是「人特别多、坐得特别远」时多找出几张脸：整帧缩到正方形之后，
    /// 后排的脸可能只剩几个像素；分块之后每块各自缩放，那些脸就被放大了。
    /// 相邻块之间留 20% 重叠，免得有人正好骑在切缝上被切成两半而漏检。
    /// <para/>
    /// 相邻块的重叠也意味着同一个人会被检出两次，交给 <see cref="FaceModel.Merge"/> 去重。
    /// </remarks>
    public static IEnumerable<FaceBox> Tiles(int width, int height, int grid)
    {
        // 喵~防御：块数为 0 或负数时，下面的步长会变成 Infinity，循环会产出无穷多块把内存吃光。
        // 画面尺寸非正也同理。这里直接一块都不产出，调用方拿到的就是「没分块」的空集合。
        if (grid <= 0 || width <= 0 || height <= 0)
        {
            yield break;
        }

        // 相邻块之间重叠的比例。取 20%：切缝上的脸至少能完整落进其中一块。
        const double overlap = 0.2;
        // 单块的横向步长，单位：像素。
        double stepX = width / (double)grid;
        // 单块的纵向步长，单位：像素。
        double stepY = height / (double)grid;

        // 逐行生成。
        for (var row = 0; row < grid; row++)
        {
            // 逐列生成。
            for (var column = 0; column < grid; column++)
            {
                // 块的左边界：从这一列的起点再往左退一点，制造重叠；不小于 0。
                var left = (int)Math.Max(0, column * stepX - stepX * overlap);
                // 块的上边界：从这一行的起点再往上退一点，制造重叠；不小于 0。
                var top = (int)Math.Max(0, row * stepY - stepY * overlap);
                // 块的右边界：走到这一列末尾再多带一点重叠；不超过画面右边界。
                var right = (int)Math.Min(width, (column + 1) * stepX + stepX * overlap);
                // 块的下边界：同理，不超过画面下边界。
                var bottom = (int)Math.Min(height, (row + 1) * stepY + stepY * overlap);

                // 太小的块（任一边不足 32 像素）喂给模型没有意义，只会白白花一次推理时间，跳过。
                if (right - left > 32 && bottom - top > 32)
                {
                    // 分数填 0：这只是个待检测的区域，不是检出的结果，分数由模型来给。
                    yield return new FaceBox(left, top, right - left, bottom - top, 0);
                }
            }
        }
    }

    /// <summary>
    /// 把人脸框的中心换算成画面内的归一化坐标（0~1）。
    /// </summary>
    /// <remarks>
    /// 归一化之后换分辨率、换摄像头都不会影响比较结果，
    /// 因为「画面里的第几个位置」这个概念和像素数是无关的。
    /// </remarks>
    public static (double X, double Y) Center(FaceBox face, int width, int height)
    {
        // 喵~防御：画面尺寸非正时除法会产出 NaN，而 NaN 参与的每一次比较都是 false，
        // 回避逻辑会静默失效（看起来像「回避开关坏了」）。这里直接给 (0,0)。
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        // 横坐标归一化：人脸框中心相对整幅画面的比例。
        var normalizedX = (face.X + face.Width / 2.0) / width;
        // 纵坐标归一化：同理。
        var normalizedY = (face.Y + face.Height / 2.0) / height;
        return (normalizedX, normalizedY);
    }

    /// <summary>
    /// 两个归一化点是不是近到可以当成同一个人。
    /// </summary>
    /// <param name="a">第一个点的归一化坐标。</param>
    /// <param name="b">第二个点的归一化坐标。</param>
    /// <param name="threshold">判定为同一个人的距离上限，按画面对角线的比例算。</param>
    /// <remarks>
    /// 教室里人不会乱动，所以用「人脸在画面里的位置」当身份。
    /// 位置离得太近就算作本人：坐标是归一化的，人稍微歪一下、摄像头稍微晃一下都不影响判定。
    /// </remarks>
    public static bool Near((double X, double Y) a, (double X, double Y) b, double threshold)
    {
        // 横向差值的平方。
        var deltaX = a.X - b.X;
        // 纵向差值的平方。
        var deltaY = a.Y - b.Y;
        // 欧氏距离小于阈值就算同一个人。
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < threshold;
    }
}
