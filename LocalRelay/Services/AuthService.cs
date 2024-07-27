using OpenShock.LocalRelay.Backend;
using OpenShock.LocalRelay.Config;
using OpenShock.SDK.CSharp.Hub;

namespace OpenShock.LocalRelay.Services;

public sealed class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly OpenShockHubClient _hubClient;
    private readonly OpenShockApi _apiClient;
    private readonly ConfigManager _configManager;

    public bool Authenticated { get; private set; }

    public AuthService(ILogger<AuthService> logger,
        OpenShockHubClient hubClient,
        OpenShockApi apiClient,
        ConfigManager configManager)
    {
        _logger = logger;
        _hubClient = hubClient;
        _apiClient = apiClient;
        _configManager = configManager;
    }

    private readonly SemaphoreSlim _authLock = new(1, 1);
    
    public async Task Authenticate()
    {
        await _authLock.WaitAsync();
        try
        {
            if (Authenticated) return;
            Authenticated = false;

            _logger.LogInformation("Setting up api client");
            _apiClient.SetupApiClient();

            _logger.LogInformation("Refreshing shockers");
            await _apiClient.RefreshShockers();

            Authenticated = true;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task Logout()
    {
        Authenticated = false;
        
        _configManager.Config.OpenShock.Token = string.Empty;
        _configManager.Save();
        
        _logger.LogInformation("Logging out");
        await _hubClient.StopAsync();
        
        _apiClient.Logout();
        
        _logger.LogInformation("Logged out");
    }
}