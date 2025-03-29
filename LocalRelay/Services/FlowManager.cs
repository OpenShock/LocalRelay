using OneOf;
using OneOf.Types;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.SDK.CSharp.Live.LiveControlModels;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Serialization.Gateway;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager
{
    private readonly ConfigManager _configManager;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;
    private readonly OpenShockApi _apiClient;

    public Guid Id { get; private set; } = Guid.Empty;

    public DeviceConnection? DeviceConnection { get; private set; } = null;
    public SerialPortClient? SerialPortClient { get; private set; } = null;
    public event Func<Task>? OnConsoleUpdate;
    
    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.Disconnected);
    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    public FlowManager(
        ConfigManager configManager,
        ILogger<FlowManager> logger,
        ILogger<DeviceConnection> deviceConnectionLogger,
        ILogger<SerialPortClient> serialPortClientLogger,
        OpenShockApi apiClient)
    {
        _configManager = configManager;
        _logger = logger;
        _deviceConnectionLogger = deviceConnectionLogger;
        _serialPortClientLogger = serialPortClientLogger;
        _apiClient = apiClient;
    }

    public async Task LoadConfigAndStart()
    {
        if (_configManager.Config.Hub.Hub != null)
            await SelectedDeviceChanged(_configManager.Config.Hub.Hub.Value);
    }
    
    public async Task<OneOf<Success, MustBeLoggedIn, AuthTokenNull, NotFound>> SelectedDeviceChanged(Guid id)
    {
        if (_apiClient.Client == null) return new MustBeLoggedIn();

        var deviceDetails = await _apiClient.Client.GetDevice(id);
        if (deviceDetails.IsT0)
        {
            var token = deviceDetails.AsT0.Value.Token;
            if (token == null) return new AuthTokenNull();

            await StartDeviceConnection(id, token);
            return new Success();
        }

        if (deviceDetails.IsT1) return new NotFound();
        if (deviceDetails.IsT2) return new MustBeLoggedIn();
        
        throw new Exception("Unhandled OneOf type");
    }

    public async Task StartDeviceConnection(Guid id, string authToken)
    {
        if (DeviceConnection != null)
        {
            await DeviceConnection.DisposeAsync();
            DeviceConnection = null;
        }
        
        Id = id;
        _state.Value = WebsocketConnectionState.Disconnected;
        _configManager.Config.Hub.Hub = id;
        _configManager.Save();
        
        DeviceConnection =
            new DeviceConnection(_configManager.Config.OpenShock.Backend, authToken, _deviceConnectionLogger);
        DeviceConnection.OnControlMessage += OnControlMessage;
        DeviceConnection.State.OnValueChanged += state =>
        {
            _state.Value = state;
            return Task.CompletedTask;
        };

        await DeviceConnection.InitializeAsync();
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

    public async Task ConnectSerialPort(string portName)
    {
        if (SerialPortClient != null)
        {
            await SerialPortClient.DisposeAsync();
            SerialPortClient = null;
        }
        
        SerialPortClient = new SerialPortClient(_serialPortClientLogger, portName);
        SerialPortClient.OnConsoleBufferUpdate += OnConsoleUpdate.Raise;
        await SerialPortClient.Open();
    }
}

public readonly struct MustBeLoggedIn;
public readonly struct DeviceNotFound;
public readonly struct AuthTokenNull;