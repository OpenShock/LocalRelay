using System.Net;
using MudBlazor.Services;
using OpenShock.LocalRelay.Backend;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Services;
using OpenShock.LocalRelay.Services.Pipes;
using OpenShock.LocalRelay.Utils;
using OpenShock.SDK.CSharp.Hub;
using OpenShock.ShockOsc.Logging;
using Serilog;

namespace OpenShock.LocalRelay;

public static class ShockOscBootstrap
{
    public static void AddShockOscServices(this IServiceCollection services)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Filter.ByExcluding(ev =>
                ev.Exception is InvalidDataException a && a.Message.StartsWith("Invocation provides")).Filter
            .ByExcluding(x => x.MessageTemplate.Text.StartsWith("Failed to find handler for"))
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
            .WriteTo.UiLogSink()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        // ReSharper disable once RedundantAssignment
        var isDebug = Environment.GetCommandLineArgs()
            .Any(x => x.Equals("--debug", StringComparison.InvariantCultureIgnoreCase));

#if DEBUG_WINDOWS || DEBUG_PHOTINO || DEBUG_WEB
        isDebug = true;
#endif
        if (isDebug)
        {
            Console.WriteLine("Debug mode enabled");
            loggerConfiguration.MinimumLevel.Verbose();
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        services.AddSerilog(Log.Logger);

        services.AddMemoryCache();

        services.AddSingleton<SerialService>();

        services.AddSingleton<PipeServerService>();

        services.AddSingleton<ConfigManager>();

        services.AddSingleton<Updater>();

        services.AddSingleton<OpenShockApi>();
        services.AddSingleton<OpenShockHubClient>();
        

        services.AddSingleton<AuthService>();
    }

    public static void AddCommonBlazorServices(this IServiceCollection services)
    {
#if DEBUG_WINDOWS || DEBUG_PHOTINO || DEBUG_WEB
        services.AddBlazorWebViewDeveloperTools();
#endif

        services.AddMudServices();
    }

    public static void StartShockOscServices(this IServiceProvider services, bool headless)
    {
        #region SystemTray

#if WINDOWS
        if (headless)
        {
            var applicationThread = new Thread(() =>
            {
                services.GetService<ITrayService>()?.Initialize();
                System.Windows.Forms.Application.Run();
            });
            applicationThread.Start();
        }
        else services.GetService<ITrayService>()?.Initialize();
#else
        services.GetService<ITrayService>()?.Initialize();
#endif

        #endregion


        // <---- Warmup ---->
        services.GetRequiredService<PipeServerService>().StartServer();

        var updater = services.GetRequiredService<Updater>();
        OsTask.Run(updater.CheckUpdate);
    }
}