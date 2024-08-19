using System.IO.Ports;
using System.Threading.Channels;
using OpenShock.LocalRelay.Config;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.SDK.CSharp.Models;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Serialization.Gateway;

namespace OpenShock.LocalRelay.Services;

public sealed class FlowManager
{
    private readonly ConfigManager _configManager;
    private readonly ILogger<FlowManager> _logger;
    private readonly ILogger<DeviceConnection> _deviceConnectionLogger;
    private readonly ILogger<SerialPortClient> _serialPortClientLogger;

    private DeviceConnection? _deviceConnection = null;
    public SerialPortClient? SerialPortClient { get; private set; } = null;
    public event Func<Task>? OnConsoleUpdate; 


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
        }
        
        SerialPortClient = new SerialPortClient(_serialPortClientLogger, portName);
        SerialPortClient.OnConsoleBufferUpdate += OnConsoleUpdate.Raise;
        await SerialPortClient.Open();
    }
}