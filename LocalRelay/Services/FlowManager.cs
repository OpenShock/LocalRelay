using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.MinimalEvents;
using OpenShock.SDK.CSharp.Live.LiveControlModels;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Serialization.Gateway;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager
{
    private readonly IModuleConfig<LocalRelayConfig> _config;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;
    private readonly IOpenShockService _openShockService;

    public Guid HubId { get; private set; } = Guid.Empty;

    public DeviceConnection? DeviceConnection { get; private set; } = null;
    public SerialPortClient? SerialPortClient { get; private set; } = null;
    
    public IAsyncMinimalEventObservable OnConsoleBufferUpdate => _onConsoleBufferUpdate;
    private readonly AsyncMinimalEvent _onConsoleBufferUpdate = new();
    
    
    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.Disconnected);
    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    public FlowManager(
        IModuleConfig<LocalRelayConfig> config,
        ILogger<FlowManager> logger,
        ILogger<DeviceConnection> deviceConnectionLogger,
        ILogger<SerialPortClient> serialPortClientLogger,
        IOpenShockService openShockService)
    {
        _config = config;
        _logger = logger;
        _deviceConnectionLogger = deviceConnectionLogger;
        _serialPortClientLogger = serialPortClientLogger;
        _openShockService = openShockService;
    }

    public async Task LoadConfigAndStart()
    {
        if (_config.Config.Hub.Hub != null)
            await SelectedDeviceChanged(_config.Config.Hub.Hub.Value);
    }
    
    public async Task SelectedDeviceChanged(Guid id)
    {
        _config.Config.Hub.Hub = id;
        await _config.Save();
        
        HubId = id;
        
        if (HubId == Guid.Empty)
        {
            _logger.LogError("Id is empty, stopping connection");
            await StopHubConnection();
            return;
        }
        
        _logger.LogInformation("Selected device changed to {Id}", id);
        var deviceDetails = await _openShockService.Api.GetHub(id);

        
        if (deviceDetails.IsT0)
        {
            var token = deviceDetails.AsT0.Value.Token;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Token is null or empty, make sure your api token has device.auth permission");
                return;
            }
            
            _logger.LogDebug("Starting device connection");

            await StartHubConnection(id, token);
            return;
        }

       
        deviceDetails.Switch(success => {}, found =>
        {
            _logger.LogError("Hub not found");
        },
        error =>
        {
            _logger.LogError("Unauthorized, make sure your logged in");
        });
        
        throw new Exception("Unhandled OneOf type");
    }

    private async Task<bool> StopHubConnection()
    {
        if (DeviceConnection == null) return false;
        await DeviceConnection.DisposeAsync();
        DeviceConnection = null;
        _state.Value = WebsocketConnectionState.Disconnected;
        return true;
    }

    private async Task StartHubConnection(Guid id, string authToken)
    {
        await StopHubConnection();
        
        DeviceConnection =
            new DeviceConnection(_openShockService.Auth.BackendBaseUri, authToken, _deviceConnectionLogger);
        DeviceConnection.OnControlMessage += OnControlMessage;
        await DeviceConnection.State.Updated.SubscribeAsync(state =>
        {
            _state.Value = state;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await DeviceConnection.InitializeAsync().ConfigureAwait(false);
    }

    private async Task OnControlMessage(ShockerCommandList commandList)
    {
        if (SerialPortClient == null) return;

        var transmitTasks = commandList.Commands.Select(command => SerialPortClient.Control(new RfTransmit
        {
            Id = command.Id,
            Intensity = command.Intensity,
            Model = command.Model,
            DurationMs = command.Duration,
            Type = command.Type
        }));
        await Task.WhenAll(transmitTasks);
    }
    
    private IAsyncDisposable? _onConsoleBufferUpdateDisposable = null;

    public async Task ConnectSerialPort(string portName)
    {
        if (SerialPortClient != null)
        {
            if(_onConsoleBufferUpdateDisposable != null) await _onConsoleBufferUpdateDisposable.DisposeAsync();
            await SerialPortClient.DisposeAsync();
            SerialPortClient = null;
        }
        
        SerialPortClient = new SerialPortClient(_serialPortClientLogger, portName);
        _onConsoleBufferUpdateDisposable = await SerialPortClient.OnConsoleBufferUpdate.SubscribeAsync(_onConsoleBufferUpdate.InvokeAsyncParallel);
        await SerialPortClient.Open();
    }
}