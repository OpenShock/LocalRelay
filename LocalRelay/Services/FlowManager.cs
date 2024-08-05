using System.IO.Ports;
using System.Threading.Channels;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.SDK.CSharp.Models;
using OpenShock.Serialization.Gateway;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager
{
    private readonly ConfigManager _configManager;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;

    private DeviceConnection? _deviceConnection = null;
    private SerialPortClient? _serialPortClient = null;


    public FlowManager(
        ConfigManager configManager,
        ILogger<FlowManager> logger,
        ILogger<DeviceConnection> deviceConnectionLogger,
        ILogger<SerialPortClient> serialPortClientLogger)
    {
        _configManager = configManager;
        _logger = logger;
        _deviceConnectionLogger = deviceConnectionLogger;
        _serialPortClientLogger = serialPortClientLogger;
    }

    public async Task StartDeviceConnection(string authToken)
    {
        _deviceConnection =
            new DeviceConnection(_configManager.Config.OpenShock.Backend, authToken, _deviceConnectionLogger);
        _deviceConnection.OnControlMessage += OnControlMessage;

        await _deviceConnection.InitializeAsync();
    }

    private async Task OnControlMessage(ShockerCommandList commandList)
    {
        if (_serialPortClient == null) return;

        var transmitTasks = commandList.Commands.Select(command => _serialPortClient.Control(new RfTransmit
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
        _serialPortClient = new SerialPortClient(_serialPortClientLogger, portName);
        await _serialPortClient.Open();
    }
}