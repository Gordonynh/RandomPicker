using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 人脸框几何计算的测试：letterbox 缩放、放大裁切、分块切分、距离判定。
/// </summary>
/// <remarks>
/// 这些函数是纯粹的数字进、数字出，不需要摄像头也不需要模型，
/// 正好把「相机抽人」里最容易出错、又最难靠肉眼发现的那部分（坐标算错）钉死在测试里。
/// </remarks>
public sealed class FaceGeometryTests
{
    #region letterbox（等比缩放 + 补边）

    [Fact]
    public void ComputeLetterbox_WideFrame_PadsTopAndBottom()
    {
        // 摄像头常见画面 1920×1080（16:9），要塞进 640×640。
        var letterbox = FaceGeometry.ComputeLetterbox(1920, 1080, 640);

        // 长边 1920 缩到 640，缩放倍数是 1/3。
        Assert.Equal(640, letterbox.FitWidth);
        // 短边按同样倍数缩，落在 360。
        Assert.Equal(360, letterbox.FitHeight);
        // 横向正好填满，不需要左右补边。
        Assert.Equal(0, letterbox.PadX);
        // 上下各补 (640-360)/2 = 140 像素的灰边。
        Assert.Equal(140, letterbox.PadY);
        // 缩放倍数精确到小数点后 5 位。
        Assert.Equal(1.0 / 3.0, letterbox.Zoom, 5);
    }

    [Fact]
    public void ComputeLetterbox_TallFrame_PadsLeftAndRight()
    {
        // 竖屏画面（手机或竖装的摄像头）：1080×1920。
        var letterbox = FaceGeometry.ComputeLetterbox(1080, 1920, 640);

        // 这次是短边先到 640。
        Assert.Equal(360, letterbox.FitWidth);
        Assert.Equal(640, letterbox.FitHeight);
        // 左右各补 140。
        Assert.Equal(140, letterbox.PadX);
        // 上下不补。
        Assert.Equal(0, letterbox.PadY);
    }

    [Fact]
    public void ComputeLetterbox_SquareFrame_NeedsNoPadding()
    {
        // 正方形画面，倍数正好是 1。
        var letterbox = FaceGeometry.ComputeLetterbox(640, 640, 640);

        Assert.Equal(640, letterbox.FitWidth);
        Assert.Equal(640, letterbox.FitHeight);
        Assert.Equal(0, letterbox.PadX);
        Assert.Equal(0, letterbox.PadY);
        Assert.Equal(1.0, letterbox.Zoom, 5);
    }

    [Fact]
    public void ComputeLetterbox_KeepsAspectRatio()
    {
        // 一张很不常见的比例：2000×500。
        var letterbox = FaceGeometry.ComputeLetterbox(2000, 500, 640);

        // 原始宽高比是 4:1，缩放之后必须还是 4:1（允许取整带来 1 像素误差）。
        var sourceRatio = 2000.0 / 500.0;
        var fittedRatio = letterbox.FitWidth / (double)letterbox.FitHeight;

        Assert.True(Math.Abs(sourceRatio - fittedRatio) < 0.05,
            $"缩放必须等比：原始 {sourceRatio:F3}，缩放后 {fittedRatio:F3}");
    }

    [Fact]
    public void ComputeLetterbox_ZeroSourceSize_ReturnsSafeFallback()
    {
        // 画面尺寸为 0（摄像头没给出有效帧）。
        var letterbox = FaceGeometry.ComputeLetterbox(0, 0, 640);

        // 不能算出 Infinity 或 NaN，要给一个最小的合法结果。
        Assert.Equal(1, letterbox.FitWidth);
        Assert.Equal(1, letterbox.FitHeight);
        Assert.Equal(0, letterbox.PadX);
        Assert.Equal(0, letterbox.PadY);
        Assert.Equal(1.0, letterbox.Zoom, 5);
    }

    [Fact]
    public void ComputeLetterbox_NegativeInputSize_ReturnsSafeFallback()
    {
        // 模型边长被传成负数（不该发生，但纯函数要自己挡住）。
        var letterbox = FaceGeometry.ComputeLetterbox(1920, 1080, -640);

        Assert.Equal(1, letterbox.FitWidth);
        Assert.Equal(1.0, letterbox.Zoom, 5);
    }

    #endregion

    #region 放大裁切

    [Fact]
    public void Expand_AppliesConfiguredFactors()
    {
        // 默认倍数：横向 1.8 倍、纵向 2.4 倍。
        var settings = new PickerSettings();
        // 一张 50×50 的脸，位于 (100,100)，画面 1000×1000。
        var face = new FaceBox(100, 100, 50, 50, 0.9f);

        var expanded = FaceGeometry.Expand(face, 1000, 1000, settings);

        // 横向 50 × 1.8 = 90。
        Assert.Equal(90, expanded.Width);
        // 纵向 50 × 2.4 = 120（比横向留得多，才装得下头顶和肩膀）。
        Assert.Equal(120, expanded.Height);
        // 水平中心不变：100 + 50/2 = 125，左边界 = 125 - 45 = 80。
        Assert.Equal(80, expanded.X);
        // 纵向中心往下挪到人脸框的 62%：100 + 31 = 131，上边界 = 131 - 60 = 71。
        Assert.Equal(71, expanded.Y);
        // 分数沿用原来的，不参与几何计算。
        Assert.Equal(0.9f, expanded.Score);
    }

    [Fact]
    public void Expand_ClampsToFrameEdges()
    {
        // 一张贴着右下角的脸。
        var face = new FaceBox(980, 980, 40, 40, 0.8f);

        var expanded = FaceGeometry.Expand(face, 1000, 1000, new PickerSettings());

        // 放大之后不能超出画面，右边界最多到 1000。
        Assert.True(expanded.Right <= 1000, $"右边界越界：{expanded.Right}");
        // 下边界同理。
        Assert.True(expanded.Bottom <= 1000, $"下边界越界：{expanded.Bottom}");
        // 也不能算成 0 像素高宽，否则后面裁不出图。
        Assert.True(expanded.Width >= 1);
        Assert.True(expanded.Height >= 1);
    }

    [Fact]
    public void Expand_OutOfRangeFactors_AreClamped()
    {
        // 用户把倍数拖到离谱的值（配置被手改也一样）。
        var settings = new PickerSettings { CropWidthFactor = 99, CropHeightFactor = 99 };
        var face = new FaceBox(100, 100, 50, 50, 0.9f);

        var expanded = FaceGeometry.Expand(face, 1000, 1000, settings);

        // 横向被夹到上限 4 倍：50 × 4 = 200。
        Assert.Equal(200, expanded.Width);
        // 纵向被夹到上限 5 倍：50 × 5 = 250。
        Assert.Equal(250, expanded.Height);
    }

    [Fact]
    public void Expand_TinyFactor_DoesNotShrinkBelowOnePixel()
    {
        // 倍数被设成 0.1（比 1 还小，会把脸本身切掉）。
        var settings = new PickerSettings { CropWidthFactor = 0.1, CropHeightFactor = 0.1 };
        var face = new FaceBox(500, 500, 40, 40, 0.9f);

        var expanded = FaceGeometry.Expand(face, 1000, 1000, settings);

        // 下限是 1 倍：脸再小也原样裁，不会缩成 0 像素。
        Assert.Equal(40, expanded.Width);
        Assert.Equal(40, expanded.Height);
    }

    [Fact]
    public void Expand_ZeroSizedFace_StillReturnsUsableBox()
    {
        // 模型偶尔会吐出宽高为 0 的框（分数刚好压线时）。
        var face = new FaceBox(10, 10, 0, 0, 0.5f);

        var expanded = FaceGeometry.Expand(face, 1000, 1000, new PickerSettings());

        // 不能崩，也不能返回 0 像素的框。
        Assert.True(expanded.Width >= 1);
        Assert.True(expanded.Height >= 1);
    }

    #endregion

    #region 抠像素

    [Fact]
    public void Crop_ExtractsRequestedRegion()
    {
        // 造一张 4×4 的图，每个像素的颜色都编码了它自己的坐标。
        var pixels = CreatePatternPixels(4, 4);

        // 抠出从 (1,1) 开始的 2×2 区域。
        var (cropped, width, height) = FaceGeometry.Crop(pixels, 4, 4, new FaceBox(1, 1, 2, 2, 0));

        // 尺寸对得上。
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        // 左上角那个像素应当来自原图的 (1,1)。
        Assert.Equal((1, 1), ReadPixel(cropped, width, 0, 0));
        // 右下角那个像素应当来自原图的 (2,2)。
        Assert.Equal((2, 2), ReadPixel(cropped, width, 1, 1));
    }

    [Fact]
    public void Crop_RegionBeyondBottomRight_IsClamped()
    {
        var pixels = CreatePatternPixels(4, 4);

        // 区域从右下角开始、还想往外扩 5 像素。
        var (cropped, width, height) = FaceGeometry.Crop(pixels, 4, 4, new FaceBox(3, 3, 5, 5, 0));

        // 只能裁出画面里真实存在的那 1 个像素。
        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal((3, 3), ReadPixel(cropped, width, 0, 0));
    }

    [Fact]
    public void Crop_NegativeOrigin_IsClamped()
    {
        var pixels = CreatePatternPixels(4, 4);

        // 区域起点跑到画面左上角之外。
        var (_, width, height) = FaceGeometry.Crop(pixels, 4, 4, new FaceBox(-5, -5, 2, 2, 0));

        // 起点被夹回 (0,0)，尺寸保持不变，不抛异常。
        Assert.Equal(2, width);
        Assert.Equal(2, height);
    }

    [Fact]
    public void Crop_NullPixels_ReturnsSingleTransparentPixel()
    {
        // 整帧像素为 null（取帧失败时的极端情况）。
        var (cropped, width, height) = FaceGeometry.Crop(null!, 4, 4, new FaceBox(0, 0, 2, 2, 0));

        // 退回 1×1，而不是抛异常。
        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(4, cropped.Length);
    }

    [Fact]
    public void Crop_ArrayShorterThanFrame_ReturnsSingleTransparentPixel()
    {
        // 数组长度根本装不下声明的帧尺寸（宽高和实际数据对不上）。
        var pixels = new byte[10];

        var (_, width, height) = FaceGeometry.Crop(pixels, 100, 100, new FaceBox(0, 0, 10, 10, 0));

        Assert.Equal(1, width);
        Assert.Equal(1, height);
    }

    [Fact]
    public void Crop_ZeroSizedFrame_ReturnsSingleTransparentPixel()
    {
        // 帧尺寸为 0。
        var (_, width, height) = FaceGeometry.Crop([], 0, 0, new FaceBox(0, 0, 10, 10, 0));

        Assert.Equal(1, width);
        Assert.Equal(1, height);
    }

    #endregion

    #region 分块

    [Fact]
    public void Tiles_ProducesFullGrid()
    {
        // 3×3 的网格应当产出 9 块。
        var tiles = FaceGeometry.Tiles(640, 640, 3).ToList();

        Assert.Equal(9, tiles.Count);
    }

    [Fact]
    public void Tiles_StayInsideFrame()
    {
        const int width = 640;
        const int height = 480;

        // 逐块检查边界。
        foreach (var tile in FaceGeometry.Tiles(width, height, 4))
        {
            // 左上角不能是负数。
            Assert.True(tile.X >= 0 && tile.Y >= 0, $"左上角越界：({tile.X},{tile.Y})");
            // 右下角不能超出画面。
            Assert.True(tile.Right <= width, $"右边界越界：{tile.Right}");
            Assert.True(tile.Bottom <= height, $"下边界越界：{tile.Bottom}");
        }
    }

    [Fact]
    public void Tiles_NeighbouringTilesOverlap()
    {
        // 取 3×3 的第一行。
        var tiles = FaceGeometry.Tiles(640, 640, 3).ToList();
        // 第一行的三块。
        var firstRow = tiles.Take(3).ToList();

        // 相邻两块必须重叠，否则正好骑在切缝上的人会被切成两半而漏检。
        for (var i = 1; i < firstRow.Count; i++)
        {
            Assert.True(firstRow[i].X < firstRow[i - 1].Right,
                $"第 {i} 块没有和前一块重叠：{firstRow[i].X} >= {firstRow[i - 1].Right}");
        }
    }

    [Fact]
    public void Tiles_TooSmallTiles_AreSkipped()
    {
        // 60×60 切成 4×4：每块只有 18~21 像素，喂给模型没有意义（模型输入是 640×640，
        // 这种块放大之后全是马赛克）。
        var tiles = FaceGeometry.Tiles(60, 60, 4).ToList();

        // 一块都不产出，省下无谓的推理时间。
        Assert.Empty(tiles);
    }

    [Fact]
    public void Tiles_PartiallyTooSmall_KeepsOnlyBigEnoughOnes()
    {
        // 100×100 切成 4×4：中间两块够大，贴着四边的那些太窄会被跳过。
        var tiles = FaceGeometry.Tiles(100, 100, 4).ToList();

        // 确实产出了可用的块。
        Assert.NotEmpty(tiles);
        // 但比完整的 16 块少——太小的那些被剔掉了。
        Assert.True(tiles.Count < 16, $"应当跳过过小的块，实际产出 {tiles.Count} 块");
        // 留下的每一块都必须够大。
        Assert.All(tiles, tile => Assert.True(tile.Width > 32 && tile.Height > 32,
            $"留下了过小的块：{tile.Width}×{tile.Height}"));
    }

    [Fact]
    public void Tiles_ZeroGrid_YieldsNothing()
    {
        // 块数为 0：不做防御的话步长会变成 Infinity，循环产出无穷多块。
        Assert.Empty(FaceGeometry.Tiles(640, 640, 0));
    }

    [Fact]
    public void Tiles_NegativeGrid_YieldsNothing()
    {
        // 负数同理。
        Assert.Empty(FaceGeometry.Tiles(640, 640, -3));
    }

    [Fact]
    public void Tiles_ZeroSizedFrame_YieldsNothing()
    {
        // 画面尺寸为 0。
        Assert.Empty(FaceGeometry.Tiles(0, 0, 3));
    }

    [Fact]
    public void Tiles_FillScoreIsZero()
    {
        // 分块只是「待检测的区域」，分数应当留 0，由模型来给。
        var tiles = FaceGeometry.Tiles(640, 640, 3).ToList();

        Assert.All(tiles, tile => Assert.Equal(0f, tile.Score));
    }

    #endregion

    #region 位置判定

    [Fact]
    public void Center_NormalizesToUnitRange()
    {
        // 1000×1000 画面里，位于 (100,100) 的 50×50 脸，中心是 (125,125)。
        var face = new FaceBox(100, 100, 50, 50, 0.9f);

        var center = FaceGeometry.Center(face, 1000, 1000);

        // 归一化之后是 0.125。
        Assert.Equal(0.125, center.X, 5);
        Assert.Equal(0.125, center.Y, 5);
    }

    [Fact]
    public void Center_ZeroSizedFrame_ReturnsOrigin()
    {
        // 画面尺寸为 0 时不能算出 NaN——NaN 参与的比较全是 false，会让回避逻辑静默失效。
        var center = FaceGeometry.Center(new FaceBox(10, 10, 5, 5, 0.9f), 0, 0);

        Assert.Equal(0, center.X);
        Assert.Equal(0, center.Y);
    }

    [Fact]
    public void Near_WithinThreshold_IsTrue()
    {
        // 两个点相距 0.02，判定阈值是 0.045。
        Assert.True(FaceGeometry.Near((0.10, 0.50), (0.12, 0.50), 0.045));
    }

    [Fact]
    public void Near_BeyondThreshold_IsFalse()
    {
        // 相距约 0.28，远超过阈值，不该被当成同一个人。
        Assert.False(FaceGeometry.Near((0.10, 0.10), (0.30, 0.30), 0.045));
    }

    [Fact]
    public void Near_DiagonalDistance_IsMeasuredProperly()
    {
        // 横向差 0.03、纵向差 0.03，直线距离约 0.0424，刚好在阈值之内。
        Assert.True(FaceGeometry.Near((0.0, 0.0), (0.03, 0.03), 0.045));
        // 阈值收到 0.04 就应当判为不同的人。
        Assert.False(FaceGeometry.Near((0.0, 0.0), (0.03, 0.03), 0.04));
    }

    #endregion

    #region 测试辅助

    /// <summary>
    /// 造一张每个像素都独一无二的 BGRA 图：B 通道放横坐标、G 通道放纵坐标。
    /// </summary>
    /// <remarks>
    /// 这样裁完之后只要读一下像素值，就能反推出它来自原图的哪个位置，
    /// 比拿常量色块去比对可靠得多。
    /// </remarks>
    private static byte[] CreatePatternPixels(int width, int height)
    {
        // 每像素 4 字节。
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // 这个像素在数组里的起始下标。
                var offset = (y * width + x) * 4;
                // B 通道记横坐标。
                pixels[offset] = (byte)x;
                // G 通道记纵坐标。
                pixels[offset + 1] = (byte)y;
                // R 通道留 0。
                pixels[offset + 2] = 0;
                // A 通道不透明。
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>读出某个像素里编码的原始坐标。</summary>
    private static (int X, int Y) ReadPixel(byte[] pixels, int width, int x, int y)
    {
        // 目标像素在数组里的起始下标。
        var offset = (y * width + x) * 4;
        // B 通道是横坐标，G 通道是纵坐标。
        return (pixels[offset], pixels[offset + 1]);
    }

    #endregion
}
