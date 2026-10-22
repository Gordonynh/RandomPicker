using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>一个小组：id、显示名，以及按名单行序排列的成员。</summary>
public sealed record PickerGroup(string Id, string Name, IReadOnlyList<string> Members);

/// <summary>
/// 小组名单（<c>小组.txt</c>）的读取、聚合与抽选。
/// </summary>
/// <remarks>
/// 成员<b>不写在小组.txt 里</b>，而是取自名单.txt 行尾的小组 id（<c>张三 G1</c>）——
/// 换组只改名单里那一行，不用两份文件来回同步。小组.txt 只管 id → 组名：
/// 一行一个组，首个空白分隔的 token 是 id，后面整体是组名。
/// <para/>
/// 候选池是两边 id 的<b>并集</b>：小组.txt 里列了但名单里没人的组不参与抽取
/// （抽出来一串空成员没有意义）；名单里有 id 却没在小组.txt 登记的组照样能抽，
/// 组名直接用那个 id——忘建组不至于让写了的组人间蒸发。
/// <para/>
/// 抽选规则和名单那套一致（随机回避上一个 / 本轮内不重复），
/// 但进度记在 <see cref="PickerSettings.DrawnGroupsThisRound"/> 上，和人员进度分开。
/// </remarks>
public sealed class GroupService : IDisposable
{
    private static readonly char[] Whitespace = [' ', '\t'];

    private readonly string _groupsPath;
    private readonly RosterService _roster;
    private FileSystemWatcher? _watcher;

    /// <summary>小组.txt 里的 id → 组名，保持文件里的行序。</summary>
    private List<(string Id, string Name)> _definitions = new();

    /// <summary>可抽取的小组（已经有成员的那些）。</summary>
    private List<PickerGroup> _groups = new();

    public GroupService(RosterService roster, string groupsPath)
    {
        _roster = roster;
        _groupsPath = groupsPath;

        EnsureGroupsFile();
        Reload();
        StartWatching();

        // 成员在名单里，名单变了小组也得跟着重建。
        _roster.RosterChanged += OnRosterChanged;
    }

    /// <summary>小组名单文件的完整路径。</summary>
    public string GroupsPath => _groupsPath;

    /// <summary>当前可抽取的小组。</summary>
    public IReadOnlyList<PickerGroup> Groups => _groups;

    /// <summary>小组发生变化（两个文件任一改动）后触发。</summary>
    public event EventHandler? GroupsChanged;

    private void EnsureGroupsFile()
    {
        if (File.Exists(_groupsPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_groupsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_groupsPath, string.Join(Environment.NewLine,
        [
            "# 小组名单：一行一个组，格式是「小组id 组名」，如：",
            "# G1 第一小组",
            "# 成员不写在这里——名单.txt 里名字后面带 id 就算进组（张三 G1）。",
            "# 保存后立即生效。"
        ]), new UTF8Encoding(false));
    }

    /// <summary>重新读取小组.txt 并按两份文件重建小组。</summary>
    public void Reload()
    {
        try
        {
            _definitions = ParseDefinitions();
        }
        catch (IOException)
        {
            // 和名单一样：保存的瞬间被占用就先用上一次的内容，下次变更再读。
        }

        Rebuild();
    }

    /// <summary>从小组.txt 解析 id → 组名。同一个 id 后面的行覆盖前面的。</summary>
    private List<(string Id, string Name)> ParseDefinitions()
    {
        var result = new List<(string Id, string Name)>();
        if (!File.Exists(_groupsPath))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(_groupsPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // 组名可能带空格，所以 id 取第一段、组名取剩下的整体。
            var splitAt = line.IndexOfAny(Whitespace);
            var id = splitAt < 0 ? line : line[..splitAt].Trim();
            var name = splitAt < 0 ? line : line[(splitAt + 1)..].Trim();
            if (id.Length == 0)
            {
                continue;
            }

            if (name.Length == 0)
            {
                name = id;
            }

            var existing = result.FindIndex(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (existing >= 0)
            {
                result[existing] = (id, name);
            }
            else
            {
                result.Add((id, name));
            }
        }

        return result;
    }

    /// <summary>把小组.txt 的定义和名单里的成员合成可抽取的小组列表。</summary>
    private void Rebuild()
    {
        // 名单里的 id → 成员，按名单行序。组序也先按名单里 id 首次出现的顺序。
        var membersById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rosterOrder = new List<string>();

        foreach (var entry in _roster.Entries)
        {
            if (entry.GroupId is null)
            {
                continue;
            }

            if (!membersById.TryGetValue(entry.GroupId, out var members))
            {
                members = new List<string>();
                membersById[entry.GroupId] = members;
                rosterOrder.Add(entry.GroupId);
            }

            members.Add(entry.Name);
        }

        // 候选 id：小组.txt 行序在前，名单里独有的 id 按出现顺序接在后面。
        var nameById = _definitions.ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
        var ids = new List<string>(_definitions.Select(x => x.Id));
        foreach (var id in rosterOrder)
        {
            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        var groups = new List<PickerGroup>();
        foreach (var id in ids)
        {
            if (!membersById.TryGetValue(id, out var members) || members.Count == 0)
            {
                continue;
            }

            // 小组.txt 没登记的 id 直接拿 id 当组名。
            var name = nameById.TryGetValue(id, out var defined) ? defined : id;
            groups.Add(new PickerGroup(id, name, members));
        }

        _groups = groups;
        GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_groupsPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_groupsPath))
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
            // 监视不了就算了，右键菜单里还有「重新载入小组」。
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 记事本保存会连着触发好几次，稍等一下再读，顺便避开写入未完成的时刻。
        System.Threading.Thread.Sleep(120);
        Reload();
    }

    private void OnRosterChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>
    /// 抽一个小组。
    /// </summary>
    /// <param name="settings">插件设置，抽选状态（本轮已抽、上次抽到谁）会就地更新。</param>
    /// <returns>抽中的小组；没有可抽的组时返回 <c>null</c>。</returns>
    /// <remarks>规则与 <see cref="RosterService.Pick"/> 一致，随机数同样走 <see cref="RandomNumberGenerator"/>。</remarks>
    public PickerGroup? Pick(PickerSettings settings)
    {
        if (_groups.Count == 0)
        {
            return null;
        }

        if (_groups.Count == 1)
        {
            settings.LastPickedGroup = _groups[0].Name;
            return _groups[0];
        }

        var candidates = BuildCandidates(settings);
        var picked = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];

        if (settings.Mode == PickMode.NoRepeat)
        {
            settings.DrawnGroupsThisRound.Add(picked.Name);
        }

        settings.LastPickedGroup = picked.Name;
        return picked;
    }

    private List<PickerGroup> BuildCandidates(PickerSettings settings)
    {
        if (settings.Mode == PickMode.NoRepeat)
        {
            // 本轮没抽过的组。抽完一轮就清空重来。
            var remaining = _groups
                .Where(x => !settings.DrawnGroupsThisRound.Contains(x.Name, StringComparer.Ordinal))
                .ToList();
            if (remaining.Count > 0)
            {
                return remaining;
            }

            settings.DrawnGroupsThisRound.Clear();
            remaining = [.._groups];

            // 新一轮的第一抽回避上一轮最后一个人，免得看起来像「连着抽到同一个」。
            if (remaining.Count > 1 && settings.LastPickedGroup is { } last)
            {
                remaining.RemoveAll(x => string.Equals(x.Name, last, StringComparison.Ordinal));
            }

            return remaining;
        }

        // 纯随机模式：只回避和上一次同一组。
        var pool = _groups.ToList();
        if (settings.LastPickedGroup is { } previous)
        {
            pool.RemoveAll(x => string.Equals(x.Name, previous, StringComparison.Ordinal));
        }

        return pool.Count > 0 ? pool : [.._groups];
    }

    public PickerGroup? PeekRandom(PickerSettings settings)
    {
        if (_groups.Count == 0)
        {
            return null;
        }

        var candidates = BuildCandidates(settings);
        return candidates.Count > 0 ? candidates[RandomNumberGenerator.GetInt32(candidates.Count)] : null;
    }

    /// <summary>「不重复」模式下手动开始新一轮。</summary>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnGroupsThisRound.Clear();
    }

    /// <summary>本轮还剩多少组没抽到。</summary>
    public int RemainingInRound(PickerSettings settings)
    {
        if (settings.Mode != PickMode.NoRepeat)
        {
            return _groups.Count;
        }

        return Math.Max(0, _groups.Count - _groups.Count(x =>
            settings.DrawnGroupsThisRound.Contains(x.Name, StringComparer.Ordinal)));
    }

    public void Dispose()
    {
        _roster.RosterChanged -= OnRosterChanged;

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
}
