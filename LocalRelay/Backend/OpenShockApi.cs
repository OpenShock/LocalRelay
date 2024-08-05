using OneOf;
using OneOf.Types;
using OpenShock.LocalRelay.Config;
using OpenShock.SDK.CSharp;
using OpenShock.SDK.CSharp.Models;
using OpenShock.SDK.CSharp.Utils;

namespace OpenShock.LocalRelay.Backend;

public sealed class OpenShockApi
{
    private readonly ILogger<OpenShockApi> _logger;
    private readonly ConfigManager _configManager;
    public OpenShockApiClient? Client { get; private set; }

    /// <summary>
    /// DI constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configManager"></param>
    public OpenShockApi(ILogger<OpenShockApi> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        SetupApiClient();
    }

    public void SetupApiClient()
    {
        Client = new OpenShockApiClient(new ApiClientOptions
        {
            Server = _configManager.Config.OpenShock.Backend,
            Token = _configManager.Config.OpenShock.Token
        });
    }
    
    public event Func<IReadOnlyCollection<ResponseDeviceWithShockers>, Task>? OnDevicesUpdated; 

    public IReadOnlyCollection<ResponseDeviceWithShockers> Devices = Array.Empty<ResponseDeviceWithShockers>(); 

    public async Task RefreshShockers()
    {
        if (Client == null)
        {
            _logger.LogError("Client is not initialized!");
            throw new Exception("Client is not initialized!");
        }
        var response = await Client.GetOwnShockers();
        
        response.Switch(success =>
            {
                Devices = success.Value;
                OnDevicesUpdated.Raise(Devices);
            },
        error =>
        {
            _logger.LogError("We are not authenticated with the OpenShock API!");
            throw new UnauthenticatedException();
        });
    }

    public void Logout()
    {
        Devices = Array.Empty<ResponseDeviceWithShockers>();
        OnDevicesUpdated.Raise(Devices);
    }

    public
        Task<OneOf<Success<LcgResponse>, NotFound, DeviceOffline, DeviceNotConnectedToGateway, UnauthenticatedError>>
        GetDeviceGateway(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (Client == null)
        {
            _logger.LogError("Client is not initialized!");
            throw new Exception("Client is not initialized!");
        }
        
        return Client.GetDeviceGateway(deviceId, cancellationToken);
    }
        
}