using System;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.UriNavigation;
using ClassIsland.RandomPicker.Models;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 注册 <c>classisland://plugins/randompicker/...</c> 这一族 URL。
/// </summary>
/// <remarks>
/// 走宿主公开的 <see cref="IUriNavigationService.HandlePluginsNavigation"/>，
/// 不自己起协议、不改注册表：ClassIsland 已经把 <c>classisland://</c> 注册好了，
/// 外部（快捷方式、浏览器、命令行、别的程序通过 IPC）打开这个 URL 就会走到这里。
/// <para/>
/// 只注册 <c>randompicker</c> 一个节点，子路径从
/// <see cref="UriNavigationEventArgs.ChildrenPathPatterns"/> 里读，
/// 这样 <c>/photo</c>、<c>/roster</c>、<c>/multi</c> 都能用，也不会和别的插件抢路径。
/// </remarks>
public sealed class PickerUriService : IHostedService
{
    private const string BasePath = "randompicker";

    private readonly IUriNavigationService _uri;
    private readonly PickerEventService _events;
    private readonly PickerHostService _host;

    public PickerUriService(IUriNavigationService uri, PickerEventService events, PickerHostService host)
    {
        _uri = uri;
        _events = events;
        _host = host;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _uri.HandlePluginsNavigation(BasePath, OnNavigated);
        }
        catch (ArgumentException)
        {
            // 同一个进程里插件被重新初始化过（路径已注册）。忽略即可，处理器是同一份逻辑。
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnNavigated(UriNavigationEventArgs args)
    {
        if (!_host.Settings.EnableUriPick)
        {
            return;
        }

        // 子路径 + 查询串一起交给解析器；sub 形如 ["photo"] 或 []。
        var sub = args.ChildrenPathPatterns.Count > 0
            ? args.ChildrenPathPatterns[^1]
            : string.Empty;
        // 没有子路径时宿主的 ChildrenPathPatterns 里会留着最后一段本身（"randompicker"）。
        if (string.Equals(sub, BasePath, StringComparison.OrdinalIgnoreCase))
        {
            sub = string.Empty;
        }

        // 悬浮窗显隐路由：/show、/hide、/toggle。不走抽选解析器，
        // 直接落到宿主的显隐方法——和快捷键、自动化行动共用同一份逻辑。
        switch (sub.ToLowerInvariant())
        {
            case "show":
                _host.SetFloatingWindowVisible(true);
                return;
            case "hide":
                _host.SetFloatingWindowVisible(false);
                return;
            case "toggle":
                _host.ToggleFloatingWindowVisible();
                return;
        }

        var path = sub.Length > 0 ? "/" + BasePath + "/" + sub : "/" + BasePath;

        var request = PickRequestParser.Parse(path, args.Uri.Query, _host.Settings, "uri");
        _events.Request(request);
    }
}
