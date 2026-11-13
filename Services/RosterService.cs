using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>名单里的一项：名字，以及名单文件里写的权重（没写就是 1）。</summary>
public sealed record RosterEntry(string Name, double Weight);

/// <summary>
/// 名单的读取与抽选。
/// </summary>
/// <remarks>
/// 名单就是一个纯文本文件，一行一个名字。没有引号、没有 JSON、没有转义，
/// 用记事本就能改。空行和以 <c>#</c> 开头的行会被忽略，重复的名字只算一个。
/// <para/>
/// <b>权重：</b>行尾写 <c>*N</c> 就能给这个人加权，例如 <c>张三 *3</c> 表示权重 3
/// （大约是不加权的人的三倍机会）。N 可以是小数，写成 <c>张三*3</c> 也认。
/// 没写就是 1。名字里含有 <c>*</c> 但后面不是纯数字的（如 <c>A*B</c>）仍按普通名字处理。
/// 设置页里可以为每个名字单独覆盖权重，覆盖值优先于名单文件。
/// </remarks>
public class RosterService
{
    private string _rosterPath;
    private FileSystemWatcher? _watcher;
    private List<RosterEntry> _entries = new();
    private List<string> _names = new();

    /// <summary>行尾权重：<c>名字 *3</c> / <c>名字*0.5</c>。</summary>
    private static readonly Regex WeightSuffix = new(
        @"^(.*?)\s*\*\s*([0-9]+(?:\.[0-9]+)?)$", RegexOptions.Compiled);

    public RosterService(string rosterPath)
    {
        _rosterPath = rosterPath;
        EnsureRosterFile();
        Reload();
        StartWatching();
    }

    /// <summary>名单文件的完整路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>
    /// 换一份名单文件。<b>保持同一个实例不变</b>——悬浮窗和设置页都持有这个引用，
    /// 换掉实例就得逐个通知；换路径则大家都自动看到新名单。
    /// 旧的文件监视器先撤掉再挂新的，否则切换后旧文件保存还会触发刷新。
    /// </summary>
    public void SwitchTo(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_rosterPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopWatching();
        _rosterPath = path;
        EnsureRosterFile();
        Reload();
        StartWatching();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 只读一份名单里有多少个不同的名字，不建监视器。菜单里列多份名单时用。
    /// </summary>
    public static List<string> ReadNames(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var name = ParseEntry(line).Name;
                if (name.Length > 0 && seen.Add(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }
        catch (Exception)
        {
            // 读不到就当空名单，菜单里显示 0 人即可。
            return [];
        }
    }

    /// <summary>只读一份名单的人数，不建文件监视器。菜单里列多份名单时用。</summary>
    public static int CountNames(string path) => ReadNames(path).Count;

    private void StopWatching()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileTouched;
        _watcher.Created -= OnFileTouched;
        _watcher.Renamed -= OnFileTouched;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>当前名单（只含名字）。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>当前名单，带名单文件里写的原始权重。</summary>
    public IReadOnlyList<RosterEntry> Entries => _entries;

    /// <summary>名单文件发生变化并重新载入后触发。</summary>
    public event EventHandler? RosterChanged;

    private void EnsureRosterFile()
    {
        if (File.Exists(_rosterPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_rosterPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 首次运行给一份带说明的示例，用户直接往下写名字就行。
        File.WriteAllText(_rosterPath, string.Join(Environment.NewLine,
        [
            "# 一行一个名字，保存后自动生效。",
            "# 以 # 开头的行和空行会被忽略。",
            "# 行尾写 *N 可以给这个人加权，例如：张三 *3",
            "# 权重也能在「应用设置 → 随机抽选 → 名单权重」里逐个调。",
            "",
            "张三",
            "李四",
            "王五"
        ]), new UTF8Encoding(false));
    }

    /// <summary>
    /// 重新读取名单文件。
    /// </summary>
    public void Reload()
    {
        try
        {
            var lines = File.Exists(_rosterPath)
                ? File.ReadAllLines(_rosterPath, Encoding.UTF8)
                : [];

            var seen = new HashSet<string>(StringComparer.Ordinal);
            _entries = lines
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith('#'))
                .Select(ParseEntry)
                .Where(x => x.Name.Length > 0 && seen.Add(x.Name))
                .ToList();
            _names = _entries.Select(x => x.Name).ToList();
        }
        catch (IOException)
        {
            // 多半是保存的瞬间被占用了，保留上一次的名单，下一次变更再读。
        }
    }

    /// <summary>把一行文字拆成名字 + 名单内权重。</summary>
    private static RosterEntry ParseEntry(string line)
    {
        var match = WeightSuffix.Match(line);
        if (!match.Success)
        {
            return new RosterEntry(line, 1.0);
        }

        var name = match.Groups[1].Value.Trim();
        if (name.Length == 0 ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var weight) ||
            !double.IsFinite(weight) || weight <= 0)
        {
            return new RosterEntry(line, 1.0);
        }

        return new RosterEntry(name, Math.Clamp(weight, 0.01, 999));
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_rosterPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_rosterPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
        }
        catch (Exception)
        {
            // 监视不了就算了，右键菜单里还有「重新载入名单」。
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 记事本保存会连着触发好几次，稍等一下再读，顺便避开写入未完成的时刻。
        System.Threading.Thread.Sleep(120);
        Reload();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 抽一个人。
    /// </summary>
    /// <param name="settings">插件设置，抽选状态（本轮已抽、上次抽到谁）会就地更新。</param>
    /// <returns>抽中的名字；名单为空时返回 <c>null</c>。</returns>
    public string? Pick(PickerSettings settings)
    {
        var picked = PickMany(settings, 1);
        return picked.Count == 0 ? null : picked[0];
    }

    /// <summary>
    /// 一次抽多个人。同一次里不会抽到重复的名字，抽完会自动开新一轮。
    /// </summary>
    /// <param name="settings">插件设置，抽选状态会就地更新。</param>
    /// <param name="count">要抽的人数；超过名单人数时按名单人数封顶。</param>
    /// <returns>抽中的名字列表；名单为空时返回空列表。</returns>
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>，每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// 加权抽样用的是 64 位均匀随机数按权重区间落点，不是「按权重复制名字再等概率抽」，
    /// 所以小数权重、很大的权重都能用，也不会把内存撑爆。
    /// </remarks>
    public List<string> PickMany(PickerSettings settings, int count)
    {
        if (_entries.Count == 0 || count <= 0)
        {
            return [];
        }

        count = Math.Min(count, _entries.Count);
        var drawn = new HashSet<string>(settings.DrawnThisRound, StringComparer.Ordinal);
        var pool = BuildCandidates(settings, drawn);
        var result = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            if (pool.Count == 0)
            {
                // 「不重复」模式下本轮抽完了，BuildCandidates 会清掉记录、开新一轮。
                // 纯随机模式没有轮次概念，抽够就停。
                if (settings.Mode != PickMode.NoRepeat)
                {
                    break;
                }

                pool = BuildCandidates(settings, drawn);
                if (pool.Count == 0)
                {
                    break;
                }
            }

            var picked = WeightedPick(pool, settings);
            result.Add(picked.Name);
            // 逐次更新，新一轮第一抽才会回避刚刚抽到的这个人，而不是上一轮最后一个。
            settings.LastPicked = picked.Name;
            pool.RemoveAll(x => string.Equals(x.Name, picked.Name, StringComparison.Ordinal));
            if (settings.Mode == PickMode.NoRepeat)
            {
                drawn.Add(picked.Name);
            }
        }

        if (settings.Mode == PickMode.NoRepeat)
        {
            settings.DrawnThisRound = drawn.ToList();
        }

        return result;
    }

    /// <summary>按当前模式算出这一抽的候选池。<paramref name="drawn"/> 是可变的本轮记录。</summary>
    private List<RosterEntry> BuildCandidates(PickerSettings settings, HashSet<string> drawn)
    {
        if (settings.Mode == PickMode.NoRepeat)
        {
            // 本轮没抽过的人。抽完一轮就清空重来。
            var remaining = _entries.Where(x => !drawn.Contains(x.Name)).ToList();
            if (remaining.Count > 0)
            {
                return remaining;
            }

            drawn.Clear();
            remaining = [.. _entries];

            // 新一轮的第一抽回避上一轮最后一个人，免得看起来像「连着抽到同一个」。
            if (remaining.Count > 1 && settings.LastPicked is { } last)
            {
                remaining.RemoveAll(x => string.Equals(x.Name, last, StringComparison.Ordinal));
            }

            return remaining;
        }

        // 纯随机模式：只回避和上一次同一个人。
        var pool = _entries.ToList();
        if (settings.LastPicked is { } previous)
        {
            pool.RemoveAll(x => string.Equals(x.Name, previous, StringComparison.Ordinal));
        }

        return pool.Count > 0 ? pool : [.. _entries];
    }

    /// <summary>按权重在候选池里抽一个。权重全部无效时退回等概率。</summary>
    private static RosterEntry WeightedPick(List<RosterEntry> candidates, PickerSettings settings)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var weights = new double[candidates.Count];
        double total = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var entry = candidates[i];
            weights[i] = settings.GetWeight(entry.Name, entry.Weight);
            total += weights[i];
        }

        if (!double.IsFinite(total) || total <= 0)
        {
            return candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        }

        // ulong / (double)ulong.MaxValue 得到 [0,1] 的均匀值，再乘总权重落到某一段里。
        var roll = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8), 0)
                   / (double)ulong.MaxValue * total;
        double cumulative = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            cumulative += weights[i];
            if (roll < cumulative)
            {
                return candidates[i];
            }
        }

        // 浮点误差兜底。
        return candidates[^1];
    }

    /// <summary>「不重复」模式下手动开始新一轮。</summary>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnThisRound.Clear();
    }

    /// <summary>本轮还剩多少人没抽到。</summary>
    public int RemainingInRound(PickerSettings settings)
    {
        if (settings.Mode != PickMode.NoRepeat)
        {
            return _names.Count;
        }

        return Math.Max(0, _names.Count - _names.Count(x =>
            settings.DrawnThisRound.Contains(x, StringComparer.Ordinal)));
    }

    public void Dispose() => StopWatching();
}
