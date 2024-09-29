using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Timers;
using OneOf;
using OneOf.Types;
using OpenShock.LocalRelay.Models.Backend;
using OpenShock.LocalRelay.Utils;
using OpenShock.SDK.CSharp.Live.LiveControlModels;
using OpenShock.SDK.CSharp.Updatables;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Serialization.Gateway;
using Timer = System.Timers.Timer;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace OpenShock.LocalRelay;

public sealed class DeviceConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _dispose;
    private CancellationTokenSource _linked;
    private CancellationTokenSource? _currentConnectionClose = null;
    private readonly ILogger<DeviceConnection> _logger;
    private readonly Uri _backend;
    private readonly string _authToken;
    private ClientWebSocket? _clientWebSocket = null;
    private DateTimeOffset _connectedAt = DateTimeOffset.MinValue;
    
    private readonly Timer _keepAliveTimer = new(TimeSpan.FromSeconds(25))
    {
        AutoReset = true
    };

    public event Func<ShockerCommandList, Task>? OnControlMessage;

    private Channel<HubToGatewayMessage> _channel = Channel.CreateUnbounded<HubToGatewayMessage>();

    public DeviceConnection(Uri backend, string authToken,
        ILogger<DeviceConnection> logger)
    {
        _backend = backend;
        _authToken = authToken;
        _logger = logger;

        _dispose = new CancellationTokenSource();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);

        _httpClient = new HttpClient { BaseAddress = backend };
        _httpClient.DefaultRequestHeaders.Add("Device-Token", authToken);
        
        _keepAliveTimer.Elapsed += KeepAliveTimerElapsed;
    }

    private async void KeepAliveTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            _logger.LogDebug("Sending keepalive");
            await _channel.Writer.WriteAsync(new HubToGatewayMessage
            {
                Payload = new HubToGatewayMessagePayload(new KeepAlive
                {
                    Uptime = (ulong)(DateTimeOffset.UtcNow - _connectedAt).TotalSeconds
                })
            });
        } catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending keepalive");
        }
    }

    private async Task MessageLoop()
    {
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(_linked.Token))
                await FlatbufferWebSocketUtils.SendFullMessage(msg, HubToGatewayMessage.Serializer, _clientWebSocket!,
                    _linked.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Closing message loop");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in message loop");
            await Reconnect();
        }
    }

    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.Disconnected);

    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    private readonly HttpClient _httpClient;

    public struct Disposed;

    public struct Reconnecting;
    public struct LcgAssignmentFailed;
    public struct Unauthorized;
    
    
    public Task InitializeAsync() => ConnectAsync();
    

    private async Task<OneOf<Success, Disposed, Reconnecting, LcgAssignmentFailed, Unauthorized>> ConnectAsync()
    {
        if (_dispose.IsCancellationRequested)
        {
            _logger.LogWarning("Dispose requested, not connecting");
            return new Disposed();
        }
        
        _keepAliveTimer.Stop();
        

        _state.Value = WebsocketConnectionState.Connecting;
        if (_currentConnectionClose != null) await _currentConnectionClose.CancelAsync();
        _linked.Dispose();
        _currentConnectionClose?.Dispose();

        _currentConnectionClose = new CancellationTokenSource();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token, _currentConnectionClose.Token);

        _clientWebSocket?.Abort();
        _clientWebSocket?.Dispose();

        _channel = Channel.CreateUnbounded<HubToGatewayMessage>();

        SDK.CSharp.Models.BaseResponse<LcgNodeResponse> lcgNodeResponse;
        
        try
        {
            var lcgAssignment = await _httpClient.GetAsync("/1/device/assignLCG");
            
            if (!lcgAssignment.IsSuccessStatusCode)
            {
                _logger.LogError("Unsuccessful LCG assignment, [{StatusCode}]", lcgAssignment.StatusCode);

                // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                switch (lcgAssignment.StatusCode)
                {
                    case HttpStatusCode.Forbidden:
                        _logger.LogError("Forbidden, auth token seems invalid, shutting down");
                        return new Unauthorized();
                    case HttpStatusCode.ServiceUnavailable:
                        await Reconnect();
                        return new Reconnecting();
                }
                
                throw new Exception("Unknown error while assigning LCG");
            }
            
            var lcg = await lcgAssignment.Content.ReadAsStreamAsync();
            var lcgModel = await JsonSerializer.DeserializeAsync<SDK.CSharp.Models.BaseResponse<LcgNodeResponse>>(lcg, JsonUtils.JsonOptions);
            if(lcgModel?.Data == null) throw new Exception("Failed to deserialize LCG model");
            lcgNodeResponse = lcgModel;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while assigning LCG, shutting down");
            _currentConnectionClose.Dispose();
            await Reconnect();
            return new LcgAssignmentFailed();
        }
        
        _clientWebSocket = new ClientWebSocket();

        _clientWebSocket.Options.SetRequestHeader("Device-Token", _authToken);
        _clientWebSocket.Options.SetRequestHeader("User-Agent", GetUserAgent());
        _logger.LogInformation("Connecting to websocket....");
        try
        {
            await _clientWebSocket.ConnectAsync(new Uri($"wss://{lcgNodeResponse.Data.Fqdn}/1/ws/device"), _linked.Token);

            _logger.LogInformation("Connected to websocket");
            _state.Value = WebsocketConnectionState.Connected;
            _connectedAt = DateTimeOffset.UtcNow;

            OsTask.Run(ReceiveLoop, _linked.Token);
            OsTask.Run(MessageLoop, _linked.Token);
            KeepAliveTimerElapsed(null, null!);
            _keepAliveTimer.Start();

            return new Success();
        }
        catch (WebSocketException e)
        {
            if (e.Message.Contains("404"))
            {
                _logger.LogError("Device not found, shutting down");
                _dispose.Dispose();
                await OnDispose.Raise();
                throw new Exception("Device not found, shutting down");
            }

            _logger.LogError(e, "Error while connecting, retrying in 3 seconds");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while connecting, retrying in 3 seconds");
        }

        await Reconnect();
        return new Reconnecting();
    }

    private async Task Reconnect()
    {
        _logger.LogWarning("Reconnecting in 3 seconds");
        
        _state.Value = WebsocketConnectionState.Reconnecting;
        _clientWebSocket?.Abort();
        _clientWebSocket?.Dispose();
        await Task.Delay(3000, _dispose.Token);
        OsTask.Run(ConnectAsync, _dispose.Token);
    }

    private string GetUserAgent()
    {
        var liveClientAssembly = GetType().Assembly;
        var liveClientVersion = liveClientAssembly.GetName().Version!;

        var entryAssembly = Assembly.GetEntryAssembly();
        var entryAssemblyName = entryAssembly!.GetName();
        var entryAssemblyVersion = entryAssemblyName.Version;

        var runtimeVersion = RuntimeInformation.FrameworkDescription;
        if (string.IsNullOrEmpty(runtimeVersion)) runtimeVersion = "Unknown Runtime";

        return
            $"OpenShock.SDK.CSharp.Live/{liveClientVersion.Major}.{liveClientVersion.Minor}.{liveClientVersion.Build} " +
            $"({runtimeVersion}; {UserAgentUtils.GetOs()}; " +
            $"{entryAssemblyName.Name} {entryAssemblyVersion!.Major}.{entryAssemblyVersion.Minor}.{entryAssemblyVersion.Build})";
    }

    private async Task ReceiveLoop()
    {
        while (!_linked.Token.IsCancellationRequested)
        {
            try
            {
                if (_clientWebSocket!.State == WebSocketState.Aborted)
                {
                    _logger.LogWarning("Websocket connection aborted, closing loop");
                    break;
                }

                var message =
                    await FlatbufferWebSocketUtils.ReceiveFullMessageAsyncNonAlloc(_clientWebSocket,
                        GatewayToHubMessage.Serializer, _linked.Token);

                if (message.IsT2)
                {
                    if (_clientWebSocket.State != WebSocketState.Open)
                    {
                        _logger.LogWarning("Client sent closure, but connection state is not open");
                        break;
                    }

                    try
                    {
                        await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal close",
                            _linked.Token);
                    }
                    catch (OperationCanceledException e)
                    {
                        _logger.LogError(e, "Error during close handshake");
                    }

                    _logger.LogInformation("Closing websocket connection");
                    break;
                }

                message.Switch(wsRequest => { OsTask.Run(() => HandleMessage(wsRequest)); },
                    failed =>
                    {
                        _logger.LogWarning("Deserialization failed for websocket message [{Message}] \n\n {Exception}",
                            failed.Message,
                            failed.Exception);
                    },
                    _ => { });
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("WebSocket connection terminated due to close or shutdown");
                break;
            }
            catch (WebSocketException e)
            {
                if (e.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely)
                    _logger.LogError(e, "Error in receive loop, websocket exception");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while processing websocket request");
            }
        }

        await _currentConnectionClose!.CancelAsync();

        if (_dispose.IsCancellationRequested)
        {
            _logger.LogDebug("Dispose requested, not reconnecting");
            return;
        }

        _logger.LogWarning("Lost websocket connection, trying to reconnect in 3 seconds");
        _state.Value = WebsocketConnectionState.Reconnecting;

        _clientWebSocket?.Abort();
        _clientWebSocket?.Dispose();

        await Task.Delay(3000, _dispose.Token);

        OsTask.Run(ConnectAsync, _dispose.Token);
    }

    private async Task HandleMessage(GatewayToHubMessage? wsRequest)
    {
        if(wsRequest?.Payload is null) return;
        
        if(_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Received kind [{Kind}]", wsRequest.Payload.Value.Kind);
        
        switch (wsRequest.Payload.Value.Kind)
        {
            case GatewayToHubMessagePayload.ItemKind.ShockerCommandList:
                await OnControlMessage.Raise(wsRequest.Payload.Value.Item1);    
                Console.WriteLine(string.Join(';', wsRequest.Payload.Value.Item1.Commands.Select(x => x.Type)));
                break;
        }
    }

    public event Func<Task>? OnDispose;

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _keepAliveTimer.Stop();
        _keepAliveTimer.Dispose();

        await _dispose.CancelAsync();
        await OnDispose.Raise();
        _clientWebSocket?.Dispose();
    }
}