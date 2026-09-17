using System.Collections.Generic;
using System.Linq;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 人脸框类型与人脸框去重的测试。
/// </summary>
/// <remarks>
/// 分块检测会把同一个人的脸在相邻两块里各检出一次，去重这一步决定了
/// 「一个人会不会被算成两个人」，也直接影响抽中概率，所以单独钉一遍。
/// </remarks>
public sealed class FaceModelMergeTests
{
    #region 人脸框的派生属性

    [Fact]
    public void FaceBox_DerivedEdgesAndArea_AreConsistent()
    {
        // 左上角在 (10,20)、宽 30、高 40 的框。
        var box = new FaceBox(10, 20, 30, 40, 0.9f);

        // 右边界 = 左边 + 宽。
        Assert.Equal(40, box.Right);
        // 下边界 = 上边 + 高。
        Assert.Equal(60, box.Bottom);
        // 面积 = 宽 × 高。
        Assert.Equal(1200, box.Area);
    }

    [Fact]
    public void FaceBox_AreaOfLargeBox_DoesNotOverflow()
    {
        // 面积用 long 存：4K 级别的框相乘会超出 int 的范围。
        var box = new FaceBox(0, 0, 100000, 100000, 0.9f);

        // 结果应当是 100 亿，没有溢出成负数。
        Assert.Equal(10_000_000_000L, box.Area);
    }

    #endregion

    #region 去重

    [Fact]
    public void Merge_EmptyList_ReturnsEmpty()
    {
        // 一张脸都没检出。
        Assert.Empty(FaceModel.Merge([]));
    }

    [Fact]
    public void Merge_SingleBox_IsKept()
    {
        var box = new FaceBox(10, 10, 50, 50, 0.9f);

        var merged = FaceModel.Merge([box]);

        Assert.Equal([box], merged);
    }

    [Fact]
    public void Merge_NonOverlappingBoxes_AreAllKept()
    {
        // 两张离得很远的脸。
        var left = new FaceBox(0, 0, 100, 100, 0.9f);
        var right = new FaceBox(500, 0, 100, 100, 0.8f);

        var merged = FaceModel.Merge([left, right]);

        // 两个人都要算数。
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_OverlappingBoxes_KeepsHigherScoreOne()
    {
        // 同一个人的两次检出：框几乎重合，分数一高一低。
        var confident = new FaceBox(0, 0, 100, 100, 0.95f);
        var shaky = new FaceBox(10, 10, 100, 100, 0.60f);

        var merged = FaceModel.Merge([shaky, confident]);

        // 只留一个，而且是分数更高的那个。
        Assert.Single(merged);
        Assert.Equal(confident, merged[0]);
    }

    [Fact]
    public void Merge_OverlapBelowThreshold_KeepsBoth()
    {
        // 横向只重叠 20%，交并比约 0.11，远低于 0.3 的阈值。
        var first = new FaceBox(0, 0, 100, 100, 0.9f);
        var second = new FaceBox(80, 0, 100, 100, 0.8f);

        var merged = FaceModel.Merge([first, second]);

        // 这种情况多半是相邻的两个人，不能合并成一个。
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_ZeroSizedBoxes_AreDropped()
    {
        // 宽高为 0 的框（分数压线时的产物）没有任何意义。
        var degenerate = new FaceBox(50, 50, 0, 0, 0.99f);
        var normal = new FaceBox(200, 200, 60, 60, 0.8f);

        var merged = FaceModel.Merge([degenerate, normal]);

        // 只留下有效的那一个。
        Assert.Equal([normal], merged);
    }

    [Fact]
    public void Merge_NegativeSizedBoxes_AreDropped()
    {
        // 宽为负数的框属于坏数据，同样丢掉。
        var broken = new FaceBox(50, 50, -10, 30, 0.9f);
        var normal = new FaceBox(200, 200, 60, 60, 0.8f);

        var merged = FaceModel.Merge([broken, normal]);

        Assert.Equal([normal], merged);
    }

    [Fact]
    public void Merge_ResultIsSortedByScoreDescending()
    {
        // 三个互不重叠的框，分数高低交错。
        var low = new FaceBox(0, 0, 50, 50, 0.30f);
        var high = new FaceBox(200, 0, 50, 50, 0.95f);
        var middle = new FaceBox(400, 0, 50, 50, 0.60f);

        var merged = FaceModel.Merge([low, high, middle]);

        // 分数从高到低排列，界面和裁切都按这个顺序取。
        Assert.Equal([high, middle, low], merged);
    }

    [Fact]
    public void Merge_ChainedOverlaps_CollapseToOne()
    {
        // 三个框首尾相接、两两重叠：这是分块检测里最常见的情形。
        var first = new FaceBox(0, 0, 100, 100, 0.9f);
        var second = new FaceBox(15, 0, 100, 100, 0.8f);
        var third = new FaceBox(30, 0, 100, 100, 0.7f);

        var merged = FaceModel.Merge([first, second, third]);

        // 最终只剩分数最高的那一个。
        Assert.Single(merged);
        Assert.Equal(first, merged[0]);
    }

    [Fact]
    public void Merge_CustomThreshold_IsHonoured()
    {
        // 同一个重叠程度：交并比约 0.11。
        var first = new FaceBox(0, 0, 100, 100, 0.9f);
        var second = new FaceBox(80, 0, 100, 100, 0.8f);

        // 把阈值放宽到 0.1，这次就该合并成一个。
        var merged = FaceModel.Merge([first, second], iouThreshold: 0.1);

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_DoesNotModifyInputList()
    {
        // 传入的列表是调用方的，去重不该就地改动它。
        var input = new List<FaceBox>
        {
            new(0, 0, 100, 100, 0.9f),
            new(10, 10, 100, 100, 0.6f)
        };
        var snapshot = input.ToList();

        FaceModel.Merge(input);

        // 原列表一个元素都不多、也不少。
        Assert.Equal(snapshot, input);
    }

    #endregion
}
