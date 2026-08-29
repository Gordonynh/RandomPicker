using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
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

        // 插件主体。PluginConfigFolder 由宿主在实例化插件时填好，
        // 设置和名单都放在那儿，跟着插件走。
        var configFolder = PluginConfigFolder;
        services.AddHostedService(_ => new PickerHostService(configFolder));

        services.AddSettingsPage<PickerSettingsPage>();
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
