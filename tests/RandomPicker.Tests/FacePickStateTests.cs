using System;
using System.Collections.Generic;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Tests;

/// <summary>
/// 拍照抽人「回避刚抽过的人」的测试。
/// </summary>
/// <remarks>
/// 这块逻辑原来挂在一堆静态字段上，测起来会互相污染；改成实例之后每个测试各拿一份，
/// 抽选用的随机数也改成可注入，于是「挑中了谁」这件事可以被精确断言。
/// </remarks>
public sealed class FacePickStateTests
{
    /// <summary>画面宽度，单位：像素。</summary>
    private const int FrameWidth = 1000;

    /// <summary>画面高度，单位：像素。</summary>
    private const int FrameHeight = 1000;

    /// <summary>画面左侧的一张脸，归一化中心约 (0.2, 0.5)。</summary>
    private static readonly FaceBox LeftFace = new(100, 400, 200, 200, 0.9f);

    /// <summary>画面右侧的一张脸，归一化中心约 (0.8, 0.5)。</summary>
    private static readonly FaceBox RightFace = new(700, 400, 200, 200, 0.9f);

    /// <summary>紧挨着左边那张脸的另一张脸，用来验证「位置接近也算同一个人」。</summary>
    private static readonly FaceBox LeftFaceNeighbour = new(120, 400, 200, 200, 0.8f);

    #region 输入防御

    [Fact]
    public void Choose_WithNullFaces_ReturnsNull()
    {
        var state = new FacePickState();

        // 没有候选脸时返回 null，让上层走「这次抽不出人」的分支。
        Assert.Null(state.Choose(null, FrameWidth, FrameHeight, 6));
    }

    [Fact]
    public void Choose_WithEmptyFaces_ReturnsNull()
    {
        var state = new FacePickState();

        // 空列表同理：绝不能放任流程走到 GetInt32(0) 上去抛异常。
        Assert.Null(state.Choose([], FrameWidth, FrameHeight, 6));
    }

    #endregion

    #region 记录与回避

    [Fact]
    public void Choose_WithAvoidDisabled_DoesNotRecordAnything()
    {
        var state = new FacePickState();

        // 回避设为 0，也就是「完全独立随机」。
        var picked = state.Choose([LeftFace, RightFace], FrameWidth, FrameHeight, 0, _ => 0);

        // 人还是要抽出来的。
        Assert.Equal(LeftFace, picked);
        // 但不该留下任何记录——关掉回避就该是纯独立随机。
        Assert.Equal(0, state.RecentCount);
    }

    [Fact]
    public void Choose_WithAvoidEnabled_RecordsPickedFace()
    {
        var state = new FacePickState();

        state.Choose([LeftFace, RightFace], FrameWidth, FrameHeight, 6, _ => 0);

        // 抽过的人被记下来了。
        Assert.Equal(1, state.RecentCount);
    }

    [Fact]
    public void Choose_AvoidsFacePickedLastTime()
    {
        var state = new FacePickState();
        var faces = new List<FaceBox> { LeftFace, RightFace };

        // 第一抽：两张脸都参与，注入的选择器固定取第一个。
        var candidateCountWhenFirst = -1;
        var first = state.Choose(faces, FrameWidth, FrameHeight, 1,
            count => { candidateCountWhenFirst = count; return 0; });

        // 第一次确实有两个人可选，抽中的是左边那位。
        Assert.Equal(2, candidateCountWhenFirst);
        Assert.Equal(LeftFace, first);

        // 第二抽：左边那位应当被剔出候选池。
        var candidateCountWhenSecond = -1;
        var second = state.Choose(faces, FrameWidth, FrameHeight, 1,
            count => { candidateCountWhenSecond = count; return 0; });

        // 候选只剩一个，而且不是刚抽过的那位。
        Assert.Equal(1, candidateCountWhenSecond);
        Assert.Equal(RightFace, second);
    }

    [Fact]
    public void Choose_NearbyFaceOfSamePerson_IsAlsoAvoided()
    {
        var state = new FacePickState();
        // 左边两张脸其实是同一个人（位置只差 2%），右边那位是另一个人。
        var faces = new List<FaceBox> { LeftFace, LeftFaceNeighbour, RightFace };

        // 先抽中左边那位。
        state.Choose(faces, FrameWidth, FrameHeight, 1, _ => 0);

        // 第二抽时，他本人和紧挨着他的那张框都该被回避掉，只剩右边那位。
        var candidateCount = -1;
        var second = state.Choose(faces, FrameWidth, FrameHeight, 1,
            count => { candidateCount = count; return 0; });

        Assert.Equal(1, candidateCount);
        Assert.Equal(RightFace, second);
    }

    [Fact]
    public void Choose_KeepsOnlyMostRecentFaces()
    {
        var state = new FacePickState();
        // 三张相距够远的脸，各自算一个人。
        var faces = new List<FaceBox>
        {
            new(100, 400, 200, 200, 0.9f),
            new(400, 400, 200, 200, 0.9f),
            new(700, 400, 200, 200, 0.9f)
        };

        // 回避数设为 2，但连着抽三次。
        state.Choose(faces, FrameWidth, FrameHeight, 2, _ => 0);
        state.Choose(faces, FrameWidth, FrameHeight, 2, _ => 0);
        state.Choose(faces, FrameWidth, FrameHeight, 2, _ => 0);

        // 记录最多只留最近 2 条，不能无限增长。
        Assert.Equal(2, state.RecentCount);
    }

    [Fact]
    public void Choose_WhenEveryoneIsAvoided_FallsBackToAllFaces()
    {
        var state = new FacePickState();
        var faces = new List<FaceBox> { LeftFace, RightFace };

        // 回避数设得比在场人数还多，等于「所有人都在回避名单上」。
        state.Choose(faces, FrameWidth, FrameHeight, 5, _ => 0);
        state.Choose(faces, FrameWidth, FrameHeight, 5, _ => 0);

        // 第三抽时一个候选都不剩了。
        var candidateCount = -1;
        var third = state.Choose(faces, FrameWidth, FrameHeight, 5,
            count => { candidateCount = count; return 0; });

        // 宁可重复也要抽得出人：退回全体重挑。
        Assert.NotNull(third);
        Assert.Equal(2, candidateCount);
        // 退回之后记录被清空重来，所以现在只记着最新抽到的这一个。
        Assert.Equal(1, state.RecentCount);
    }

    [Fact]
    public void Choose_WithNegativeAvoid_BehavesLikeNoAvoid()
    {
        var state = new FacePickState();

        // 配置被手改成负数时，按「不回避」处理，而不是拿负数去裁剪列表。
        state.Choose([LeftFace, RightFace], FrameWidth, FrameHeight, -3, _ => 0);

        Assert.Equal(0, state.RecentCount);
    }

    [Fact]
    public void Forget_ClearsAllRecords()
    {
        var state = new FacePickState();

        // 先攒两条记录。
        state.Choose([LeftFace, RightFace], FrameWidth, FrameHeight, 6, _ => 0);
        state.Choose([LeftFace, RightFace], FrameWidth, FrameHeight, 6, _ => 1);
        Assert.True(state.RecentCount > 0);

        // 换摄像头或用户手动重来时清空。
        state.Forget();

        Assert.Equal(0, state.RecentCount);
    }

    #endregion

    #region 选择器防御

    [Fact]
    public void Choose_WhenSelectorReturnsOutOfRangeIndex_IsClamped()
    {
        var state = new FacePickState();
        var faces = new List<FaceBox> { LeftFace, RightFace };

        // 注入一个乱给下标的选择器，模拟「将来换了实现但写错了」。
        var picked = state.Choose(faces, FrameWidth, FrameHeight, 0, _ => 999);

        // 不能因此越界崩溃，夹回合法范围后仍然返回一张真实存在的脸。
        Assert.Contains(picked, faces);
    }

    #endregion
}
