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
using OpenShock.Internal.Common.Utils;

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

    private readonly SemaphoreSlim _txResponseSemaphore = new(0, 1);

    private int _closeSignalled;

    private readonly record struct TxCommand(byte[] Data, bool WaitForResponse);

    private readonly Channel<TxCommand> _txChannel = Channel.CreateUnbounded<TxCommand>(new UnboundedChannelOptions()
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
            WriteBufferSize = 16 * 1024,
            NewLine = "\r\n",
            Parity = Parity.None,
            Handshake = Handshake.None,
            DtrEnable = true
        };
    }


    public async Task Open()
    {
        if (_currentCts != null) await _currentCts.CancelAsync();
        _linkedCts.Dispose();
        _currentCts?.Dispose();


        _currentCts = new CancellationTokenSource();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, _currentCts.Token);
        Interlocked.Exchange(ref _closeSignalled, 0);

        _logger.LogInformation("Opening serial port {PortName}", _serialPort.PortName);
        _serialPort.Open();

        var token = _linkedCts.Token;

        _ = OsTask.Run(() => TxLoop(token));

        _ = OsTask.Run(() => RxLoop(token));

        // Catches the port being closed without an IO error ever surfacing on the Rx side.
        _ = OsTask.Run(async () =>
        {
            try
            {
                while (_serialPort.IsOpen)
                {
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogTrace("Detected serial port closed");
            await SignalClosed();
        });
    }

    public ValueTask QueueCommand(string command)
    {
        return _txChannel.Writer.WriteAsync(new TxCommand(Encoding.ASCII.GetBytes(command), false));
    }

    private static readonly byte[] Space = [0x20];
    private static readonly byte[] RfTransmitCommand = "rftransmit"u8.ToArray();
    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    /// <summary>
    /// Fires <see cref="OnClose"/> exactly once for this client and cancels its loops. Every path
    /// that notices the port is gone funnels through here, otherwise a yanked USB cable leaves the
    /// relay looking connected forever.
    /// </summary>
    private async Task SignalClosed()
    {
        if (Interlocked.Exchange(ref _closeSignalled, 1) != 0) return;

        _logger.LogDebug("Serial port {PortName} closed", _serialPort.PortName);

        var cts = _currentCts;
        if (cts != null)
        {
            try
            {
                if (!cts.IsCancellationRequested) await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down
            }
        }

        try
        {
            await _onClose.InvokeAsyncParallel();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while invoking OnClose");
        }
    }

    private async Task TxLoop(CancellationToken token)
    {
        try
        {
            var stream = _serialPort.BaseStream;

            await foreach (var txCommand in _txChannel.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (txCommand.WaitForResponse)
                    {
                        // Drain any stale semaphore signals before writing
                        await _txResponseSemaphore.WaitAsync(0);
                    }

                    await stream.WriteAsync(txCommand.Data, token);
                    await stream.FlushAsync(token);

                    _logger.LogDebug("Wrote command to serial port: {Command}", Encoding.ASCII.GetString(txCommand.Data));

                    if (txCommand.WaitForResponse)
                    {
                        // Wait briefly for the ESP32 to respond
                        if (!await _txResponseSemaphore.WaitAsync(100, token))
                        {
                            // Response may be stuck in USB-serial bridge TX buffer.
                            // Send a bare \r\n to nudge the bridge into flushing.
                            await stream.WriteAsync(LineEnd, token);
                            await stream.FlushAsync(token);

                            if (!await _txResponseSemaphore.WaitAsync(2000, token))
                            {
                                _logger.LogWarning("Timed out waiting for device response, proceeding with next command");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException e)
                {
                    // The port is gone, no point draining the rest of the queue into it.
                    _logger.LogError(e, "IO error during TxLoop, closing serial port");
                    break;
                }
                catch (InvalidOperationException e)
                {
                    _logger.LogError(e, "Serial port no longer open during TxLoop");
                    break;
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
            return;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Fatal error during TxLoop");
        }

        _logger.LogDebug("TxLoop exited");
        await SignalClosed();
    }

    private async Task RxLoop(CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (_serialPort.IsOpen && !token.IsCancellationRequested)
            {
                try
                {
                    var data = await _serialPort.BaseStream.ReadAsync(buffer, token);
                    HandleRxChars(buffer.AsSpan()[..data]);
                    _terminalUpdate.OnNext(0);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogTrace("RxLoop cancelled. Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, token.IsCancellationRequested);
                    return;
                }
                catch (Exception e)
                {
                    // Unplugging the device lands here. SerialPort.IsOpen stays true afterwards,
                    // so the close has to be signalled from this path or nothing ever reconnects.
                    _logger.LogError(e, "Error during RxLoop, closing serial port");
                    break;
                }
            }

            _logger.LogTrace("Serial Port exited. Serial Port Open: {Open} | Cancelled: {Cancelled}", _serialPort.IsOpen, token.IsCancellationRequested);

            await SignalClosed();
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

            string completedLine;
            var lastItem = RxConsoleBuffer.IsEmpty ? null : RxConsoleBuffer.Back();
            if (lastItem != null && !lastItem.EndsWith('\n'))
            {
                var line = remainingChars[..toWriteChars];
                RxConsoleBuffer.PopBack();
                completedLine = lastItem + line.ToString();
                RxConsoleBuffer.PushBack(completedLine);
            }
            else
            {
                completedLine = remainingChars[..toWriteChars].ToString();
                RxConsoleBuffer.PushBack(completedLine);
            }

            // Signal TxLoop when the device sends a response
            if (completedLine.Contains("$SYS$|"))
            {
                if (_txResponseSemaphore.CurrentCount == 0)
                    _txResponseSemaphore.Release();
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

            await _txChannel.Writer.WriteAsync(new TxCommand(controlCommand, true));

            _logger.LogDebug("Queued rftransmit {@Command}", rfTransmit);
        } catch (Exception e)
        {
            _logger.LogError(e, "Error during Control");
        }
    }


    public async Task Close()
    {
        _logger.LogDebug("Closing serial port {PortName}", _serialPort.PortName);

        try
        {
            if (_serialPort.IsOpen) _serialPort.Close();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while closing serial port");
        }

        await SignalClosed();
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

        _txChannel.Writer.TryComplete();

        _serialPort.Dispose();

        if (_currentCts != null) await _currentCts.CancelAsync();
        await _disposeCts.CancelAsync();

        _terminalUpdate.Dispose();
        _txResponseSemaphore.Dispose();
        _linkedCts.Dispose();
        _currentCts?.Dispose();
        _disposeCts.Dispose();
    }
}
