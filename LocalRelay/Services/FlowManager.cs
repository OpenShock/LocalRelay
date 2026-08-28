using LucHeart.WebsocketLibrary;
using Microsoft.Extensions.Logging;
using OpenShock.Desktop.ModuleBase.Api;
using OpenShock.Desktop.ModuleBase.Config;
using OpenShock.Internal.Common.Utils;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.MinimalEvents;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.Serialization.Gateway;
using ShockerCommandList = OpenShock.Serialization.Deprecated.DoNotUse.V1.ShockerCommandList;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager : IAsyncDisposable
{
    private static readonly TimeSpan MaxSerialRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxHubRetryDelay = TimeSpan.FromSeconds(60);

    private readonly IModuleConfig<LocalRelayConfig> _config;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;
    private readonly IOpenShockService _openShockService;
    private readonly SerialService _serialService;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _serialConnectLock = new(1, 1);
    private int _serialReconnectRunning;
    private bool _disposed;

    public Guid HubId { get; private set; } = Guid.Empty;

    public DeviceConnection? DeviceConnection { get; private set; } = null;
    public SerialPortClient? SerialPortClient { get; private set; } = null;

    public IAsyncMinimalEventObservable OnConsoleBufferUpdate => _onConsoleBufferUpdate;
    private readonly AsyncMinimalEvent _onConsoleBufferUpdate = new();


    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.Disconnected);
    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    /// <summary>
    /// Whether a serial device is currently open. The UI needs this to follow along when the
    /// reconnect loop attaches or loses a device on its own.
    /// </summary>
    public IAsyncUpdatable<bool> SerialConnected => _serialConnected;
    private readonly AsyncUpdatableVariable<bool> _serialConnected = new(false);

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
        // Both sides retry on their own. Neither is allowed to stop the other from starting,
        // the usual startup failure is simply "no network yet" or "device not plugged in yet".
        if (_config.Config.Hub.Hub != null)
        {
            var hubId = _config.Config.Hub.Hub.Value;
            _ = OsTask.Run(() => StartHubConnectionWithRetry(hubId));
        }

        if (!_config.Config.Serial.AutoConnect) return;

        try
        {
            if (await TryAutoConnectSerialPort()) return;

            _logger.LogInformation("No matching serial device present, waiting for one to appear");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to auto connect serial port on startup");
        }

        StartSerialReconnect();
    }

    /// <summary>
    /// Keeps retrying until the hub connection is actually established. Without this a relay that
    /// autostarts before the network is up never connects at all.
    /// </summary>
    private async Task StartHubConnectionWithRetry(Guid hubId)
    {
        for (var attempt = 0; !_shutdown.IsCancellationRequested; attempt++)
        {
            try
            {
                if (await SelectedDeviceChanged(hubId)) return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while starting hub connection");
            }

            var delay = TimeSpan.FromSeconds(Math.Min(5 * (attempt + 1), MaxHubRetryDelay.TotalSeconds));
            _logger.LogWarning("Could not start hub connection, retrying in {Delay:0}s", delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Connects to the configured serial device if it is currently present.
    /// </summary>
    /// <returns>True if a port was connected.</returns>
    private async Task<bool> TryAutoConnectSerialPort()
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
                return true;
            }

            _logger.LogDebug("Could not find serial device with VID:{Vid:X4} PID:{Pid:X4}",
                serialConfig.Vid.Value, serialConfig.Pid.Value);
        }

        // Fallback to saved port name
        if (string.IsNullOrWhiteSpace(serialConfig.Port)) return false;

        // Only attempt it if the port is actually there, so a retry loop does not throw every pass
        if (!_serialService.GetSerialPorts()
                .Any(p => string.Equals(p.Port, serialConfig.Port, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("Saved port {Port} is not present", serialConfig.Port);
            return false;
        }

        _logger.LogInformation("Falling back to saved port {Port}", serialConfig.Port);
        await ConnectSerialPort(serialConfig.Port);
        return true;
    }

    /// <returns>True if a hub connection was started.</returns>
    public async Task<bool> SelectedDeviceChanged(Guid id)
    {
        _config.Config.Hub.Hub = id;
        await _config.Save();

        HubId = id;

        if (HubId == Guid.Empty)
        {
            _logger.LogInformation("Id is empty, stopping connection");
            await StopHubConnection();
            return true;
        }

        _logger.LogInformation("Selected device changed to {Id}", id);
        var deviceDetails = await _openShockService.Api.GetHub(id);

        if (deviceDetails.IsT0)
        {
            var token = deviceDetails.AsT0.Value.Token;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Token is null or empty, make sure your api token has device.auth permission");
                return false;
            }

            _logger.LogDebug("Starting device connection");

            await StartHubConnection(id, token);
            return true;
        }

        if (deviceDetails.IsT1) _logger.LogError("Hub not found");
        else _logger.LogError("Unauthorized, make sure your logged in");

        await StopHubConnection();
        return false;
    }

    private IAsyncDisposable? _deviceStateSubscription = null;

    private async Task<bool> StopHubConnection()
    {
        if (_deviceStateSubscription != null)
        {
            await _deviceStateSubscription.DisposeAsync();
            _deviceStateSubscription = null;
        }

        if (DeviceConnection == null)
        {
            _state.Value = WebsocketConnectionState.Disconnected;
            return false;
        }

        await DeviceConnection.DisposeAsync();
        DeviceConnection = null;
        _state.Value = WebsocketConnectionState.Disconnected;
        return true;
    }

    private async Task StartHubConnection(Guid id, string authToken)
    {
        await StopHubConnection();

        var deviceConnection =
            new DeviceConnection(_openShockService.Auth.BackendBaseUri, authToken, _deviceConnectionLogger);
        deviceConnection.OnControlMessage += OnControlMessage;
        _deviceStateSubscription = await deviceConnection.State.Updated.SubscribeAsync(state =>
        {
            _state.Value = state;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        DeviceConnection = deviceConnection;

        await deviceConnection.InitializeAsync().ConfigureAwait(false);
    }

    private async Task OnControlMessage(ShockerCommandList shockerCommandList)
    {
        var serialPortClient = SerialPortClient;
        if (serialPortClient == null) return;

        var transmitTasks = shockerCommandList.Commands.Select(command => serialPortClient.Control(new RfTransmit
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
        // Serialized, otherwise a click in the UI racing the reconnect loop leaves two clients
        // fighting over the same port.
        await _serialConnectLock.WaitAsync(CancellationToken.None);
        try
        {
            await ConnectSerialPortCore(portName);
        }
        finally
        {
            _serialConnectLock.Release();
        }
    }

    private async Task ConnectSerialPortCore(string? portName)
    {
        await DisposeSerialPortClient();

        if (string.IsNullOrWhiteSpace(portName)) return;

        var serialPortClient = new SerialPortClient(_serialPortClientLogger, portName);

        try
        {
            _onConsoleBufferUpdateDisposable =
                await serialPortClient.OnConsoleBufferUpdate.SubscribeAsync(_onConsoleBufferUpdate.InvokeAsyncParallel);
            _onCloseDisposable = await serialPortClient.OnClose.SubscribeAsync(OnSerialPortClosed);

            await serialPortClient.Open();
        }
        catch
        {
            // Leave no half connected client behind, the caller decides whether to retry.
            await DisposeSerialPortSubscriptions();
            await serialPortClient.DisposeAsync();
            throw;
        }

        SerialPortClient = serialPortClient;
        _serialConnected.Value = true;
    }

    private async Task DisposeSerialPortClient()
    {
        var serialPortClient = SerialPortClient;
        SerialPortClient = null;
        _serialConnected.Value = false;

        // Unsubscribe first so tearing the client down does not trip the reconnect loop.
        await DisposeSerialPortSubscriptions();

        if (serialPortClient != null) await serialPortClient.DisposeAsync();
    }

    private async Task DisposeSerialPortSubscriptions()
    {
        if (_onCloseDisposable != null)
        {
            await _onCloseDisposable.DisposeAsync();
            _onCloseDisposable = null;
        }

        if (_onConsoleBufferUpdateDisposable != null)
        {
            await _onConsoleBufferUpdateDisposable.DisposeAsync();
            _onConsoleBufferUpdateDisposable = null;
        }
    }

    private Task OnSerialPortClosed()
    {
        _logger.LogWarning("Serial port closed");
        _serialConnected.Value = false;
        StartSerialReconnect();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the serial reconnect loop if it is not already running. Runs detached so it never
    /// blocks the close event that triggered it.
    /// </summary>
    private void StartSerialReconnect()
    {
        if (_shutdown.IsCancellationRequested) return;

        if (!_config.Config.Serial.AutoConnect)
        {
            _logger.LogInformation("Auto connect is disabled, not reconnecting serial port");
            return;
        }

        if (Interlocked.CompareExchange(ref _serialReconnectRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Serial reconnect already in progress");
            return;
        }

        _ = OsTask.Run(async () =>
        {
            try
            {
                await SerialReconnectLoop();
            }
            finally
            {
                Interlocked.Exchange(ref _serialReconnectRunning, 0);
            }
        });
    }

    /// <summary>
    /// Retries indefinitely with capped backoff. The device may well be unplugged for hours,
    /// giving up permanently just means the relay never comes back without a restart.
    /// </summary>
    private async Task SerialReconnectLoop()
    {
        _logger.LogInformation("Waiting for serial device...");

        for (var attempt = 0; !_shutdown.IsCancellationRequested; attempt++)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(2 * (attempt + 1), MaxSerialRetryDelay.TotalSeconds));

            try
            {
                await Task.Delay(delay, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_config.Config.Serial.AutoConnect)
            {
                _logger.LogInformation("Auto connect is disabled, stopping serial reconnect");
                return;
            }

            try
            {
                if (await TryAutoConnectSerialPort())
                {
                    _logger.LogInformation("Serial port reconnected after {Attempt} attempt(s)", attempt + 1);
                    return;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to reconnect serial port on attempt {Attempt}", attempt + 1);
            }
        }
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _shutdown.CancelAsync();

        await StopHubConnection();

        await _serialConnectLock.WaitAsync(CancellationToken.None);
        try
        {
            await DisposeSerialPortClient();
        }
        finally
        {
            _serialConnectLock.Release();
        }

        _serialConnectLock.Dispose();
        _shutdown.Dispose();
    }
}
