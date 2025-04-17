using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using OpenShock.Desktop.ModuleBase;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.Desktop.ModuleBase.Navigation;
using OpenShock.LocalRelay;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Services;
using OpenShock.LocalRelay.Ui.Pages.Dash.Tabs;

[assembly:DesktopModule(typeof(LocalRelayModule), "OpenShock.LocalRelay", "Local Relay")]

namespace OpenShock.LocalRelay;

public class LocalRelayModule : DesktopModuleBase
{
    public override IReadOnlyCollection<NavigationItem> NavigationComponents { get; } =
    [
        new()
        {
            Name = "Hub",
            ComponentType = typeof(HubTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.Hub)
        },
        new()
        {
            Name = "Serial",
            ComponentType = typeof(SerialTab),
            Icon = IconOneOf.FromSvg(Icons.Material.Filled.VoiceChat)
        }
    ];

    public override async Task Setup()
    {
        var config = await ModuleInstanceManager.GetModuleConfig<LocalRelayConfig>();
        ModuleServiceProvider = BuildServices(config);
        
    }

    private IServiceProvider BuildServices(IModuleConfig<LocalRelayConfig> config)
    {
        var loggerFactory = ModuleInstanceManager.AppServiceProvider.GetRequiredService<ILoggerFactory>();
        
        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton(config);

        services.AddSingleton(ModuleInstanceManager.OpenShock);
        
        services.AddSingleton<FlowManager>();
        services.AddSingleton<SerialService>();
        
        return services.BuildServiceProvider();
    }   

    public override async Task Start()
    {
        await ModuleServiceProvider.GetRequiredService<FlowManager>().LoadConfigAndStart();
    }
}