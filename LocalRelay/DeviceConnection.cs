using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using LucHeart.WebsocketLibrary;
using LucHeart.WebsocketLibrary.Flatbuffers;
using LucHeart.WebsocketLibrary.Updatables;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;
using OpenShock.Internal.Common.Utils;
using OpenShock.LocalRelay.Models.Backend;
using OpenShock.LocalRelay.Utils;
using OpenShock.SDK.CSharp.Utils;
using OpenShock.Serialization.Deprecated.DoNotUse.V1;

namespace OpenShock.LocalRelay;

/// <summary>
/// Hub side of the relay. Owns the LCG assignment and the keepalive, the connection lifetime and
/// reconnect behaviour come from the websocket client.
/// </summary>
public sealed class DeviceConnection : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan LcgAssignmentTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<DeviceConnection> _logger;
    private readonly HttpClient _httpClient;
    private readonly FlatbufferWebsocketClient<GatewayToHubMessage, HubToGatewayMessage> _client;
    private readonly CancellationTokenSource _dispose = new();
    private readonly List<IAsyncDisposable> _subscriptions = [];

    private DateTimeOffset _connectedAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    public event Func<ShockerCommandList, Task>? OnControlMessage;
    public event Func<Task>? OnDispose;

    public IAsyncUpdatable<WebsocketConnectionState> State => _client.State;

    public DeviceConnection(Uri backend, string authToken, ILogger<DeviceConnection> logger)
    {
        _logger = logger;

        _httpClient = new HttpClient { BaseAddress = backend, Timeout = LcgAssignmentTimeout };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", GetUserAgent());
        _httpClient.DefaultRequestHeaders.Add("Device-Token", authToken);

        var liveClientVersion = GetType().Assembly.GetName().Version!;

        _client = new FlatbufferWebsocketClient<GatewayToHubMessage, HubToGatewayMessage>(
            AssignLcgAsync,
            GatewayToHubMessage.Serializer,
            HubToGatewayMessage.Serializer,
            new WebsocketClientOptions
            {
                Logger = logger,
                Headers = new Dictionary<string, string>
                {
                    ["Device-Token"] = authToken,
                    ["Firmware-Version"] =
                        $"{liveClientVersion.Major}.{liveClientVersion.Minor}.{liveClientVersion.Build}",
                    ["User-Agent"] = GetUserAgent()
                }
            });
    }

    public async Task InitializeAsync()
    {
        _subscriptions.Add(await _client.OnMessage.SubscribeAsync(HandleMessage));
        _subscriptions.Add(await _client.OnConnected.SubscribeAsync(HandleConnected));
        _subscriptions.Add(await _client.OnConnectError.SubscribeAsync(HandleConnectError));

        _client.Start();

        _ = OsTask.Run(KeepAliveLoop);
    }

    /// <summary>
    /// Connect hook. Asks the backend which gateway to talk to, once per connection attempt.
    /// </summary>
    private async Task<OneOf<WebsocketConnectOptions, Error>> AssignLcgAsync()
    {
        try
        {
            using var lcgAssignment = await _httpClient.GetAsync("/1/device/assignLCG", _dispose.Token);

            if (!lcgAssignment.IsSuccessStatusCode)
            {
                _logger.LogError("Unsuccessful LCG assignment, [{StatusCode}]", lcgAssignment.StatusCode);

                if (lcgAssignment.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogError("Forbidden, auth token seems invalid, shutting down");
                    await ShutdownAsync();
                }

                return new Error();
            }

            await using var lcg = await lcgAssignment.Content.ReadAsStreamAsync(_dispose.Token);
            var lcgModel =
                await JsonSerializer.DeserializeAsync<SDK.CSharp.Models.BaseResponse<LcgNodeResponse>>(lcg,
                    JsonUtils.JsonOptions, _dispose.Token);

            if (string.IsNullOrEmpty(lcgModel?.Data?.Fqdn)) throw new Exception("Failed to deserialize LCG model");

            return new WebsocketConnectOptions
            {
                Uri = new Uri($"wss://{lcgModel.Data.Fqdn}/1/ws/device")
            };
        }
        catch (OperationCanceledException) when (_dispose.IsCancellationRequested)
        {
            return new Error();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while assigning LCG");
            return new Error();
        }
    }

    private async Task HandleConnected()
    {
        _connectedAt = DateTimeOffset.UtcNow;
        await SendKeepAlive();
    }

    private async Task HandleConnectError(Exception exception)
    {
        // A 404 on the gateway handshake means this hub no longer exists, retrying cannot help.
        if (exception is WebSocketException && exception.Message.Contains("404"))
        {
            _logger.LogError("Device not found, shutting down");
            await ShutdownAsync();
        }
    }

    private async Task KeepAliveLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);

            while (await timer.WaitForNextTickAsync(_dispose.Token))
            {
                if (_client.State.Value != WebsocketConnectionState.Connected) continue;
                await SendKeepAlive();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Closing keepalive loop");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in keepalive loop");
        }
    }

    private async Task SendKeepAlive()
    {
        try
        {
            var uptime = (ulong)Math.Max(0, (DateTimeOffset.UtcNow - _connectedAt).TotalSeconds);
            _logger.LogDebug("Sending keepalive: {Uptime}", uptime);

            await _client.QueueMessage(new HubToGatewayMessage
            {
                Payload = new HubToGatewayMessagePayload(new KeepAlive
                {
                    Uptime = uptime
                })
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while sending keepalive");
        }
    }

    private async Task HandleMessage(GatewayToHubMessage wsRequest)
    {
        if (wsRequest.Payload is null) return;

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Received kind [{Kind}]", wsRequest.Payload.Value.Kind);

        switch (wsRequest.Payload.Value.Kind)
        {
            case GatewayToHubMessagePayload.ItemKind.ShockerCommandList:
                await OnControlMessage.Raise(wsRequest.Payload.Value.Item1);
                break;
        }
    }

    private string GetUserAgent()
    {
        var liveClientAssembly = GetType().Assembly;
        var liveClientVersion = liveClientAssembly.GetName().Version!;

        var runtimeVersion = RuntimeInformation.FrameworkDescription;
        if (string.IsNullOrEmpty(runtimeVersion)) runtimeVersion = "Unknown Runtime";

        return
            $"LocalRelay/{liveClientVersion.Major}.{liveClientVersion.Minor}.{liveClientVersion.Build} " +
            $"({runtimeVersion}; {UserAgentUtils.GetOs()})";
    }

    /// <summary>
    /// Stops for good, for failures where reconnecting cannot help.
    /// </summary>
    private async Task ShutdownAsync()
    {
        try
        {
            if (!_dispose.IsCancellationRequested) await _dispose.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await _client.StopAsync();
        await OnDispose.Raise();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_dispose.IsCancellationRequested) await _dispose.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already gone
        }

        foreach (var subscription in _subscriptions)
        {
            try
            {
                await subscription.DisposeAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while disposing subscription");
            }
        }

        _subscriptions.Clear();

        await _client.DisposeAsync();

        await OnDispose.Raise();

        _httpClient.Dispose();
        _dispose.Dispose();
    }
}
