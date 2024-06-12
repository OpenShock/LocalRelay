using OpenShock.LocalRelay.Platforms.Windows;

namespace OpenShock.LocalRelay;

public static class HeadlessProgram
{
    public static IHost SetupHeadlessHost()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddShockOscServices();

#if WINDOWS
            services.AddWindowsServices();
#endif
        });
        
        var app = builder.Build();
        app.Services.StartShockOscServices(true);
        
        return app;
    }
}