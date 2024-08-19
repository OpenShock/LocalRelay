using System.Buffers;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CircularBuffer;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.LocalRelay.Utils;
using OpenShock.SDK.CSharp.Utils;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace OpenShock.LocalRelay;

public sealed class SerialPortClient : IAsyncDisposable
{
    private readonly ILogger<SerialPortClient> _logger;
    private readonly SerialPort _serialPort;
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _currentCts;
    private CancellationTokenSource _linkedCts;

    public readonly CircularBuffer<string> RxConsoleBuffer = new(1000);
    public event Func<Task>? OnConsoleBufferUpdate;

    public event Func<Task>? OnClose;

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

            OnClose.Raise();
            
            await _currentCts.CancelAsync();
        });
    }

    public ValueTask QueueCommand(string command)
    {
        return _txChannel.Writer.WriteAsync(Encoding.ASCII.GetBytes(command));
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
                    await stream.WriteAsync(channelCommand);
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
                    HandleRxChars(buffer.AsSpan()[..data]);
                    Console.WriteLine("Sending update lol: " + data);
                    OsTask.Run(() => OnConsoleBufferUpdate.Raise());
                }
                catch (OperationCanceledException)
                {
                    _logger.LogTrace("RxLoop cancelled. Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, _linkedCts.IsCancellationRequested);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error during RxLoop");
                }
            }
            
            _logger.LogTrace("Serial Port exited. Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, _linkedCts.IsCancellationRequested);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleRxChars(Span<byte> newCharsSpan)
    {
        if(newCharsSpan.Length < 1) return;
        var charsToWrite = Encoding.ASCII.GetCharCount(newCharsSpan);
        if(charsToWrite < 1) return;

        Span<char> newCharArray = stackalloc char[charsToWrite];
        
        Encoding.ASCII.TryGetChars(newCharsSpan, newCharArray, out _);
        
        AddToConsoleBuffer(newCharArray);
    }

    private void AddToConsoleBuffer(Span<char> remainingChars)
    {
        while (true)
        {
            var lineBreak = remainingChars.IndexOf('\n');


            var toWriteChars = remainingChars.Length;

            if (lineBreak != -1)
            {
                toWriteChars = lineBreak + 1;
            }

            var lastItem = RxConsoleBuffer.IsEmpty ? null : RxConsoleBuffer.Back();
            if (lastItem != null && !lastItem.EndsWith('\n'))
            {
                var line = remainingChars[..toWriteChars];
                RxConsoleBuffer.PopBack();
                RxConsoleBuffer.PushBack(lastItem + line.ToString());
            }
            else
            {
                RxConsoleBuffer.PushBack(remainingChars[..toWriteChars].ToString());
            }

            if (toWriteChars < remainingChars.Length)
            {
                remainingChars = remainingChars[toWriteChars..];
                continue;
            }

            break;
        }
    }

    public async Task Control(RfTransmit rfTransmit)
    {
        try
        {
            var command = JsonSerializer.SerializeToUtf8Bytes(rfTransmit, JsonUtils.JsonOptions);

            /*
             * rftransmit = 10 bytes
             * space = 1 byte
             * command json = dynamic size
             * LineEnd = 2 bytes
             */
            var controlCommand = new byte[command.Length + 10 + 1 + 2];

            RfTransmitCommand.CopyTo(controlCommand, 0);
            Space.CopyTo(controlCommand, RfTransmitCommand.Length);
            command.CopyTo(controlCommand, RfTransmitCommand.Length + Space.Length);
            LineEnd.CopyTo(controlCommand, RfTransmitCommand.Length + Space.Length + command.Length);

            await _txChannel.Writer.WriteAsync(controlCommand);

            _logger.LogDebug("Queued rftransmit {@Command}", rfTransmit);
        } catch (Exception e)
        {
            _logger.LogError(e, "Error during Control");
        }
    }


    public void Close()
    {
        _logger.LogDebug("Closing serial port {PortName}", _serialPort.PortName);
        _serialPort.Close();
        OnClose.Raise();
    }
    
    
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Close();
        _serialPort.Dispose();
        
        if (_currentCts != null) await _currentCts.CancelAsync();
        await _disposeCts.CancelAsync();
        
        _linkedCts.Dispose();
        _currentCts?.Dispose();
        _disposeCts.Dispose();
    }
}