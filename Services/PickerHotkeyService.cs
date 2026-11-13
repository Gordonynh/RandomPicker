using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.RandomPicker.Interop;
using ClassIsland.RandomPicker.Models;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 全局快捷键服务：ClassIsland 运行期间注册一个系统级热键，按下即切换悬浮窗显隐。
/// 走 Win32 全局热键，悬浮窗即使被隐藏、没有焦点也能被叫回来。
/// </summary>
public sealed class PickerHotkeyService : IHostedService
{
    private readonly PickerHostService _host;
    private GlobalHotkey? _hotkey;

    public PickerHotkeyService(PickerHostService host) => _host = host;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ApplyHotkey();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hotkey?.Dispose();
        _hotkey = null;
        return Task.CompletedTask;
    }

    /// <summary>按当前设置（开关 + 修饰键 + 虚拟键）注册或刷新热键。设置页改完会调用。</summary>
    public void ApplyHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;

        var settings = _host.Settings;
        if (!settings.HotkeyEnabled)
        {
            return;
        }

        try
        {
            _hotkey = new GlobalHotkey(
                (uint)settings.HotkeyModifiers,
                (uint)settings.HotkeyVirtualKey,
                () => Dispatcher.UIThread.Post(_host.ToggleFloatingWindowVisible));
        }
        catch (Exception)
        {
            // 注册环节出错（类/窗口建不出来）就静默失效——不影响其它功能。
        }
    }
}
