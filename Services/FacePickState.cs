using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 拍照抽人的「别老抽到同一个人」状态：记住最近抽中的人脸位置，挑人时避开它们。
/// </summary>
/// <remarks>
/// 从 <see cref="CameraPicker"/> 的静态字段里搬出来的。原来回避记录挂在 static 上，
/// 谁都没法把它清干净，测试之间会互相看到对方留下的记录；改成实例之后，
/// 每个使用者（生产代码一份、每个测试一份）各记各的。
/// <para/>
/// <b>为什么用位置当身份：</b>拍照模式原本每一张都是独立随机挑一个，没有任何记忆，
/// 于是「连着两次抽到同一个人」是必然会发生的：30 个人里撞上的概率就有 1/30，
/// 一节课抽十几次几乎一定会遇到。教室里人不会乱动，所以用<b>人脸在画面里的位置</b>
/// 当作身份——位置落在最近抽过的那几个人附近的，这一轮先不抽。
/// <para/>
/// 记录只在内存里，不写配置：摄像头一挪、人一换座位，那些坐标就没意义了，
/// 存进配置反而会把过时的回避带到下一次开机。
/// </remarks>
internal sealed class FacePickState
{
    /// <summary>
    /// 判定为「同一个人」的距离上限，按画面归一化坐标算（对角线的比例）。
    /// </summary>
    /// <remarks>
    /// 取 0.045：教室里相邻两个座位的间距大致就是画面的这个比例，
    /// 再放大就会把邻座误判成同一个人，再缩小则挡不住「稍微歪一下头」造成的偏移。
    /// </remarks>
    private const double SameFaceDistance = 0.045;

    /// <summary>最近抽中的人脸中心，按画面归一化，越靠前越早。</summary>
    private readonly List<(double X, double Y)> _recent = [];

    /// <summary>保护 <see cref="_recent"/> 的锁。抽人在后台线程跑，界面线程也可能来清记录。</summary>
    private readonly object _gate = new();

    /// <summary>当前记着几个最近抽中的人。测试和诊断用。</summary>
    public int RecentCount
    {
        get
        {
            // 读的时候也要上锁，免得读到别处正在改动的列表。
            lock (_gate)
            {
                return _recent.Count;
            }
        }
    }

    /// <summary>把回避记录清空（换了摄像头，或者用户手动要求重来时调用）。</summary>
    public void Forget()
    {
        // 加锁清空，避免和正在进行的挑人操作撞上。
        lock (_gate)
        {
            _recent.Clear();
        }
    }

    /// <summary>
    /// 从检出的人脸里挑一个，尽量避开最近抽过的那几个。
    /// </summary>
    /// <param name="faces">本次检出的人脸框，坐标系是原始画面。</param>
    /// <param name="width">画面宽度，单位：像素。</param>
    /// <param name="height">画面高度，单位：像素。</param>
    /// <param name="avoidCount">要回避最近几个；0 表示完全独立随机。</param>
    /// <param name="pickIndexSelector">
    /// 从 0 到「候选人数减一」里挑一个下标的函数。
    /// 传 <c>null</c> 就用密码学随机数（生产环境走这条）；测试传固定函数，结果才可断言。
    /// </param>
    /// <returns>挑中的人脸框；一张脸都没有时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 避开之后如果一个都不剩（人本来就少，或者回避数设得太大），就退回全体重挑——
    /// 宁可重复，也不能出现「抽不出人」。
    /// </remarks>
    public FaceBox? Choose(List<FaceBox>? faces, int width, int height, int avoidCount,
        Func<int, int>? pickIndexSelector = null)
    {
        // 喵~防御：一张可用的脸都没有时，直接返回 null，让上层走「这次抽不出人、退回名单」的分支。
        // 不能放任流程往下走——RandomNumberGenerator.GetInt32(0) 会抛 ArgumentOutOfRangeException。
        if (faces is null || faces.Count == 0)
        {
            return null;
        }

        // 回避人数下限取 0：设成负数（配置被手改坏）时按「不回避」处理，而不是当成负数去裁剪列表。
        var avoid = Math.Max(0, avoidCount);

        // 加锁：挑人的过程会读改 _recent，必须整段串行，否则两个线程可能挑中同一个人。
        lock (_gate)
        {
            // 先把记录裁到只留最近 avoid 个，超出更早的就忘掉。
            TrimTo(avoid);

            // 候选池：要回避时，把落在最近抽过的人附近的脸先剔出去；不回避时全体参与。
            var pool = avoid == 0
                ? faces
                : faces.Where(face => !IsNearRecent(face, width, height)).ToList();

            // 回避之后一个候选都不剩了：退回全体重挑，并清空记录（否则下一次还是挑不出来）。
            if (pool.Count == 0)
            {
                pool = faces;
                _recent.Clear();
            }

            // 挑一个下标：生产环境用操作系统的熵源，测试注入固定函数。
            var index = pickIndexSelector?.Invoke(pool.Count) ?? RandomNumberGenerator.GetInt32(pool.Count);
            // 喵~防御：注入的选择器可能给出越界下标（测试写错、或将来换实现），
            // 这里夹回合法范围，保证不会因为一个下标就越界访问。
            index = Math.Clamp(index, 0, pool.Count - 1);
            // 取出挑中的人脸。
            var picked = pool[index];

            // 只有开启回避时才记位置：关掉回避就该是纯独立随机，记了也没人用。
            if (avoid > 0)
            {
                // 记下这张脸在画面里的归一化中心，供后面几次挑人时回避。
                _recent.Add(FaceGeometry.Center(picked, width, height));
                // 记完再裁一次，保证记录条数不超上限。
                TrimTo(avoid);
            }

            return picked;
        }
    }

    /// <summary>把回避记录裁到只留最近 <paramref name="avoid"/> 条。</summary>
    /// <param name="avoid">保留的条数上限。</param>
    private void TrimTo(int avoid)
    {
        // 从最早的一条开始删，直到条数不超过上限。
        // 列表很短（最多几十条），这个循环不会成为瓶颈。
        while (_recent.Count > avoid)
        {
            _recent.RemoveAt(0);
        }
    }

    /// <summary>这张脸是不是落在最近抽过的那几个人附近。</summary>
    private bool IsNearRecent(FaceBox face, int width, int height)
    {
        // 先把这张脸换算成归一化中心，再逐个和最近记录比距离。
        var center = FaceGeometry.Center(face, width, height);
        // 只要有一个记录离得够近，就算「刚抽过他」。
        return _recent.Any(recent => FaceGeometry.Near(recent, center, SameFaceDistance));
    }
}
