#if WINDOWS
using OpenShock.LocalRelay.Services;

namespace OpenShock.LocalRelay.Platforms.Windows;

public static class WindowsServices
{
    public static void AddWindowsServices(this IServiceCollection services)
    {
        services.AddSingleton<ITrayService, WindowsTrayService>();
    }
}
#endif