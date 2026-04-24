using LucHeart.WebsocketLibrary;
using Microsoft.Extensions.Logging;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.MinimalEvents;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.Serialization.Gateway;
using ShockerCommandList = OpenShock.Serialization.Deprecated.DoNotUse.V1.ShockerCommandList;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager
{
    private readonly IModuleConfig<LocalRelayConfig> _config;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;
    private readonly IOpenShockService _openShockService;
    private readonly SerialService _serialService;

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
        IOpenShockService openShockService,
        SerialService serialService)
    {
        _config = config;
        _logger = logger;
        _deviceConnectionLogger = deviceConnectionLogger;
        _serialPortClientLogger = serialPortClientLogger;
        _openShockService = openShockService;
        _serialService = serialService;
    }

    public async Task LoadConfigAndStart()
    {
        if (_config.Config.Hub.Hub != null)
            await SelectedDeviceChanged(_config.Config.Hub.Hub.Value);

        var serialConfig = _config.Config.Serial;

        if (serialConfig.AutoConnect)
        {
            await AutoConnectSerialPort();
        }
    }

    private async Task AutoConnectSerialPort()
    {
        var serialConfig = _config.Config.Serial;

        // Try to find the device by VID/PID first
        if (serialConfig.Vid != null && serialConfig.Pid != null)
        {
            var match = _serialService.FindPortByVidPid(serialConfig.Vid.Value, serialConfig.Pid.Value);
            if (match != null)
            {
                _logger.LogInformation("Auto-connecting to {Name} on {Port} (VID:{Vid:X4} PID:{Pid:X4})",
                    match.Name, match.Port, match.Vid, match.Pid);
                await ConnectSerialPort(match.Port);
                return;
            }

            _logger.LogWarning("Could not find serial device with VID:{Vid:X4} PID:{Pid:X4}",
                serialConfig.Vid.Value, serialConfig.Pid.Value);
        }

        // Fallback to saved port name
        if (!string.IsNullOrWhiteSpace(serialConfig.Port))
        {
            _logger.LogInformation("Falling back to saved port {Port}", serialConfig.Port);
            await ConnectSerialPort(serialConfig.Port);
        }
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

    private async Task OnControlMessage(ShockerCommandList shockerCommandList)
    {
        if (SerialPortClient == null) return;

        var transmitTasks = shockerCommandList.Commands.Select(command => SerialPortClient.Control(new RfTransmit
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
    private IAsyncDisposable? _onCloseDisposable = null;

    public async Task ConnectSerialPort(SerialPortInfo portInfo)
    {
        // Save VID/PID and port to config
        var serialConfig = _config.Config.Serial;
        serialConfig.Port = portInfo.Port;
        serialConfig.Vid = portInfo.Vid;
        serialConfig.Pid = portInfo.Pid;
        await _config.Save();

        await ConnectSerialPort(portInfo.Port);
    }

    public async Task ConnectSerialPort(string? portName)
    {
        if (SerialPortClient != null)
        {
            if (_onCloseDisposable != null) await _onCloseDisposable.DisposeAsync();
            if (_onConsoleBufferUpdateDisposable != null) await _onConsoleBufferUpdateDisposable.DisposeAsync();
            await SerialPortClient.DisposeAsync();
            SerialPortClient = null;
        }

        if (string.IsNullOrWhiteSpace(portName)) return;

        SerialPortClient = new SerialPortClient(_serialPortClientLogger, portName);
        _onConsoleBufferUpdateDisposable = await SerialPortClient.OnConsoleBufferUpdate.SubscribeAsync(_onConsoleBufferUpdate.InvokeAsyncParallel);
        _onCloseDisposable = await SerialPortClient.OnClose.SubscribeAsync(OnSerialPortClosed);
        await SerialPortClient.Open();
    }

    private async Task OnSerialPortClosed()
    {
        _logger.LogWarning("Serial port closed, attempting to reconnect...");

        var serialConfig = _config.Config.Serial;
        if (!serialConfig.AutoConnect) return;

        // Retry reconnection with backoff
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * (attempt + 1), 10)));

            // Try VID/PID match first
            if (serialConfig.Vid != null && serialConfig.Pid != null)
            {
                var match = _serialService.FindPortByVidPid(serialConfig.Vid.Value, serialConfig.Pid.Value);
                if (match != null)
                {
                    _logger.LogInformation("Reconnecting to {Name} on {Port}", match.Name, match.Port);
                    try
                    {
                        await ConnectSerialPort(match.Port);
                        return;
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to reconnect on attempt {Attempt}", attempt + 1);
                    }

                    continue;
                }
            }

            _logger.LogDebug("Device not found, retrying... (attempt {Attempt}/10)", attempt + 1);
        }

        _logger.LogError("Failed to reconnect to serial device after 10 attempts");
    }

    public async Task DisconnectSerialPort()
    {
        var serialConfig = _config.Config.Serial;
        serialConfig.Port = null;
        serialConfig.Vid = null;
        serialConfig.Pid = null;
        await _config.Save();

        await ConnectSerialPort((string?)null);
    }
}