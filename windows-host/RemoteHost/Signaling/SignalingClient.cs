using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteHost.Signaling;

/// <summary>
/// Thin wrapper around ClientWebSocket for talking to the signaling server.
/// Raises <see cref="OnMessage"/> with the parsed top-level "type" and the
/// full JsonDocument for the caller to read further fields from.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public event Action<string, JsonElement>? OnMessage;
    public event Action? OnClosed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string wsUrl)
    {
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendAsync(object message)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var messageBuffer = new System.IO.MemoryStream();
        try
        {
            while (_ws!.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var text = Encoding.UTF8.GetString(messageBuffer.ToArray());
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement.Clone();
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    OnMessage?.Invoke(type, root);
                }
                catch (JsonException)
                {
                    // ignore malformed frames
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (WebSocketException)
        {
            // connection dropped
        }
        finally
        {
            OnClosed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_ws is { State: WebSocketState.Open })
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            _ws?.Dispose();
            _cts?.Dispose();
        }
    }
}
