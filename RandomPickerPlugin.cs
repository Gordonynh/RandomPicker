using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.RandomPicker.Automation;
using ClassIsland.RandomPicker.Services;
using ClassIsland.RandomPicker.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker;

/// <summary>
/// 随机抽选插件入口。
/// </summary>
public class RandomPickerPlugin : PluginBase
{
    private static readonly Assembly SelfAssembly = typeof(RandomPickerPlugin).Assembly;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        EnsureAssemblyResolvable();

        // 插件自带 ONNX Runtime（宿主没有）。没有 deps.json，得自己在本插件的
        // PluginLoadContext 上挂解析回调，按路径把它加载进来。
        FaceModel.EnsureManagedResolvable(SelfAssembly);

        // 提醒提供方：抽到人之后把名字推到 ClassIsland 主界面那条上。
        services.AddNotificationProvider<PickerNotificationProvider>();

        var configFolder = PluginConfigFolder;

        // 事件转接点是所有外部入口（URL / HTTP / 自动化）的公共依赖，先注册成单例。
        var events = new PickerEventService();
        services.AddSingleton(events);

        // 宿主服务同时按自身类型注册：设置页、规则、HTTP 都要能拿到同一份实例。
        var host = new PickerHostService(configFolder, events);
        services.AddSingleton(host);
        services.AddSingleton<IHostedService>(host);
        services.AddHostedService<PickerUriService>();
        // 全局快捷键：按住能切换悬浮窗显隐，窗口藏起来也照样叫得回来。
        services.AddHostedService<PickerHotkeyService>();

        services.AddSettingsPage<PickerSettingsPage>();

        // 触发器：抽选事件发生时触发工作流。
        services.AddTrigger<AnyPickedTrigger>();
        services.AddTrigger<RosterPickedTrigger>();
        services.AddTrigger<PhotoPickedTrigger>();
        services.AddTrigger<MultiPickedTrigger>();
        services.AddTrigger<NamePickedTrigger, PickerTriggerSettingsControl>();
        // 悬浮窗显隐触发器：隐藏 / 显示时各触发一次。
        services.AddTrigger<FloatingWindowHiddenTrigger>();
        services.AddTrigger<FloatingWindowShownTrigger>();

        // 行动：让自动化反过来发起一次抽选。四个共用一个设置控件。
        services.AddAction<PickOnceAction, PickerActionSettingsControl>();
        services.AddAction<RosterPickAction, PickerActionSettingsControl>();
        services.AddAction<PhotoPickAction, PickerActionSettingsControl>();
        services.AddAction<MultiPickAction, PickerActionSettingsControl>();
        services.AddAction<GroupPickAction, PickerActionSettingsControl>();
        // 悬浮窗显隐行动：隐藏 / 显示。
        services.AddAction<HideFloatingWindowAction>();
        services.AddAction<ShowFloatingWindowAction>();

        // 规则：注册之后用户就能在「规则集」里搭抽选相关条件。
        PickerRules.Register(services);
    }

    /// <summary>
    /// 让 <c>avares://ClassIsland.RandomPicker/...</c> 能被解析到。
    /// </summary>
    /// <remarks>
    /// Avalonia 的资源加载器按名字用 <see cref="Assembly.Load(AssemblyName)"/> 找程序集，走的是
    /// 默认 <see cref="AssemblyLoadContext"/>；插件却在独立的 PluginLoadContext 里，默认上下文看不到它。
    /// 设置页是 axaml，不挂这个回调就加载不出来。
    /// </remarks>
    private static void EnsureAssemblyResolvable()
    {
        var selfName = SelfAssembly.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            requested.Name == selfName ? SelfAssembly : null;
    }
}
