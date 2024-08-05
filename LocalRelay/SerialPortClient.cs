using System.Buffers;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.LocalRelay.Utils;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace OpenShock.LocalRelay;

public sealed class SerialPortClient : IAsyncDisposable
{
    private readonly ILogger<SerialPortClient> _logger;
    private readonly SerialPort _serialPort;
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _currentCts;
    private CancellationTokenSource _linkedCts;

    private readonly Channel<byte[]> _txChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions()
    {
        SingleReader = true
    });

    public SerialPortClient(ILogger<SerialPortClient> logger, string portName, uint baudRate = 115200)
    {
        _logger = logger;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);

        _serialPort = new SerialPort
        {
            PortName = portName,
            BaudRate = (int)baudRate,
            DataBits = 8,
            StopBits = StopBits.One,
            ReadTimeout = 500,
            WriteTimeout = 500,
            NewLine = "\r\n"
        };
    }


    public async Task Open()
    {
        if (_currentCts != null) await _currentCts.CancelAsync();
        _linkedCts.Dispose();
        _currentCts?.Dispose();


        _currentCts = new CancellationTokenSource();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, _currentCts.Token);

        _logger.LogInformation("Opening serial port {PortName}", _serialPort.PortName);
        _serialPort.Open();


        OsTask.Run(TxLoop);
        OsTask.Run(RxLoop);

        OsTask.Run(async () =>
        {
            while (_serialPort.IsOpen)
            {
                await Task.Delay(100);
            }
            
            _logger.LogTrace("Detected serial port closed, cancelling current CTS");

            await _currentCts.CancelAsync();
        });
    }

    private static readonly byte[] Space = [0x20];
    private static readonly byte[] RfTransmitCommand = "rftransmit"u8.ToArray();
    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    private async Task TxLoop()
    {
        var stream = _serialPort.BaseStream;

        try
        {
            await foreach (var channelCommand in _txChannel.Reader.ReadAllAsync(_linkedCts.Token))
            {
                try
                {
                    await stream.WriteAsync(RfTransmitCommand);
                    await stream.WriteAsync(Space);
                    await stream.WriteAsync(channelCommand);
                    await stream.WriteAsync(LineEnd);
                    await stream.FlushAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error during TxLoop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("TxLoop cancelled");
        }

        _logger.LogDebug("TxLoop exited");
    }

    private async Task RxLoop()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (_serialPort.IsOpen && !_linkedCts.IsCancellationRequested)
            {
                try
                {
                    var data = await _serialPort.BaseStream.ReadAsync(buffer, _linkedCts.Token);

                    Console.Write(Encoding.ASCII.GetString(buffer[..data]));
                }
                catch (OperationCanceledException e)
                {
                    _logger.LogError(e, "YES");
                    _logger.LogTrace("RxLoop cancelled. Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, _linkedCts.IsCancellationRequested);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error during RxLoop");
                }
            }
            
            _logger.LogTrace("Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, _linkedCts.IsCancellationRequested);

            Console.WriteLine("RxLoop exited");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task Control(RfTransmit rfTransmit)
    {
        var command = JsonSerializer.SerializeToUtf8Bytes(rfTransmit, JsonUtils.JsonOptions);
        await _txChannel.Writer.WriteAsync(command);

        _logger.LogTrace("Queued command {@Command}", rfTransmit);
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _serialPort.Dispose();
        
        if (_currentCts != null) await _currentCts.CancelAsync();
        await _disposeCts.CancelAsync();
        
        _linkedCts.Dispose();
        _currentCts?.Dispose();
        _disposeCts.Dispose();
    }
}