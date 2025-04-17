using System.Buffers;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CircularBuffer;
using Microsoft.Extensions.Logging;
using OpenShock.LocalRelay.Models.Serial;
using OpenShock.LocalRelay.Utils;
using OpenShock.MinimalEvents;
using OpenShock.SDK.CSharp.Utils;

namespace OpenShock.LocalRelay;

public sealed class SerialPortClient : IAsyncDisposable
{
    private readonly ILogger<SerialPortClient> _logger;
    private readonly SerialPort _serialPort;
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _currentCts;
    private CancellationTokenSource _linkedCts;
    private readonly Subject<byte> _terminalUpdate = new();

    public readonly CircularBuffer<string> RxConsoleBuffer = new(1000);

    public IAsyncMinimalEventObservable OnConsoleBufferUpdate => _onConsoleBufferUpdate;
    private readonly AsyncMinimalEvent _onConsoleBufferUpdate = new();

    public IAsyncMinimalEventObservable OnClose => _onClose;
    private readonly AsyncMinimalEvent _onClose = new();
    


    private readonly Channel<byte[]> _txChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions()
    {
        SingleReader = true
    });

    public SerialPortClient(ILogger<SerialPortClient> logger, string portName, uint baudRate = 115200)
    {
        _logger = logger;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);

        _terminalUpdate.Throttle(TimeSpan.FromMilliseconds(20)).Subscribe(u =>
        {
            OsTask.Run(() => _onConsoleBufferUpdate.InvokeAsyncParallel());
        });
        
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


#pragma warning disable CS4014
        OsTask.Run(TxLoop);

        OsTask.Run(RxLoop);

        OsTask.Run(async () =>
        {
            while (_serialPort.IsOpen)
            {
                await Task.Delay(100);
            }
            
            _logger.LogTrace("Detected serial port closed, cancelling current CTS");
            
            if (_currentCts != null && !_disposed) await _currentCts.CancelAsync();
            
            await _onClose.InvokeAsyncParallel();
        });
#pragma warning restore CS4014
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
                    _terminalUpdate.OnNext(0);
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


    public async Task Close()
    {
        _logger.LogDebug("Closing serial port {PortName}", _serialPort.PortName);
        _serialPort.Close();
        await _onClose.InvokeAsyncParallel();
    }
    
    
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await Close();
        } catch (Exception e)
        {
            _logger.LogError(e, "Error during DisposeAsync, Calling Close failed");
        }

        _serialPort.Dispose();
        
        if (_currentCts != null) await _currentCts.CancelAsync();
        await _disposeCts.CancelAsync();
        
        _linkedCts.Dispose();
        _currentCts?.Dispose();
        _disposeCts.Dispose();
    }
}