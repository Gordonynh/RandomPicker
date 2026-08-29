using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 名单的读取与抽选。
/// </summary>
/// <remarks>
/// 名单就是一个纯文本文件，一行一个名字。没有引号、没有 JSON、没有转义，
/// 用记事本就能改。空行和以 <c>#</c> 开头的行会被忽略，重复的名字只算一个。
/// </remarks>
public class RosterService
{
    private readonly string _rosterPath;
    private FileSystemWatcher? _watcher;
    private List<string> _names = new();

    public RosterService(string rosterPath)
    {
        _rosterPath = rosterPath;
        EnsureRosterFile();
        Reload();
        StartWatching();
    }

    /// <summary>名单文件的完整路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>当前名单。</summary>
    public IReadOnlyList<string> Names => _names;

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

            _names = lines
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith('#'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            // 多半是保存的瞬间被占用了，保留上一次的名单，下一次变更再读。
        }
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
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>，每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// </remarks>
    public string? Pick(PickerSettings settings)
    {
        if (_names.Count == 0)
        {
            return null;
        }

        if (_names.Count == 1)
        {
            settings.LastPicked = _names[0];
            return _names[0];
        }

        var candidates = BuildCandidates(settings);
        var picked = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];

        if (settings.Mode == PickMode.NoRepeat)
        {
            settings.DrawnThisRound.Add(picked);
        }

        settings.LastPicked = picked;
        return picked;
    }

    private List<string> BuildCandidates(PickerSettings settings)
    {
        if (settings.Mode == PickMode.NoRepeat)
        {
            // 本轮没抽过的人。抽完一轮就清空重来。
            var remaining = _names.Where(x => !settings.DrawnThisRound.Contains(x, StringComparer.Ordinal)).ToList();
            if (remaining.Count > 0)
            {
                return remaining;
            }

            settings.DrawnThisRound.Clear();
            remaining = [.._names];

            // 新一轮的第一抽回避上一轮最后一个人，免得看起来像「连着抽到同一个」。
            if (remaining.Count > 1 && settings.LastPicked is { } last)
            {
                remaining.RemoveAll(x => string.Equals(x, last, StringComparison.Ordinal));
            }

            return remaining;
        }

        // 纯随机模式：只回避和上一次同一个人。
        var pool = _names.ToList();
        if (settings.LastPicked is { } previous)
        {
            pool.RemoveAll(x => string.Equals(x, previous, StringComparison.Ordinal));
        }

        return pool.Count > 0 ? pool : [.._names];
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

    public void Dispose()
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
}
