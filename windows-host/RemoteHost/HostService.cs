using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RemoteHost.Capture;
using RemoteHost.Config;
using RemoteHost.Input;
using RemoteHost.Rtc;
using RemoteHost.Signaling;
using SIPSorcery.Net;

namespace RemoteHost;

public sealed record PendingRequest(string SessionRequestId, string ControllerEmail);

/// <summary>
/// Orchestrates the whole host-side lifecycle: connects to the signaling
/// server, requests pairing codes, surfaces incoming pairing requests for the
/// operator to accept/reject, and - once a session starts - wires screen
/// capture into the WebRTC video track and the data channel's control
/// messages into synthetic input. All state transitions are driven by
/// authenticated signaling server messages; nothing here ever accepts a
/// control command that didn't arrive over an active, approved session's
/// data channel.
/// </summary>
public sealed class HostService : IAsyncDisposable
{
    private readonly HostConfig _config;
    private readonly SignalingClient _signaling = new();
    private readonly InputInjector _input = new();
    private readonly Dictionary<string, string> _pendingEmailByRequestId = new();
    private readonly Stopwatch _frameClock = new();

    private PeerConnectionManager? _peer;
    private ScreenCapture? _capture;
    private string? _activeSessionId;
    private long _lastFramePushedMs;
    private List<RTCIceServer> _iceServers = new();
    private bool _reconnecting;

    public event Action<bool>? OnConnectedChanged;
    public event Action<string, long>? OnPairingCode; // code, expiresAtEpochMs
    public event Action<PendingRequest>? OnPairingRequest;
    public event Action<string>? OnPairingRequestExpired; // sessionRequestId
    public event Action<string, string>? OnSessionStarted; // sessionId, controllerEmail
    public event Action<string>? OnSessionEnded; // reason
    public event Action<string>? OnStatus;

    public HostService(HostConfig config)
    {
        _config = config;
        _signaling.OnMessage += HandleMessage;
        _signaling.OnClosed += HandleDisconnected;
    }

    public async Task StartAsync()
    {
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        OnStatus?.Invoke("Connecting to signaling server...");
        await _signaling.ConnectAsync(_config.ServerWsUrl);
        await _signaling.SendAsync(new { type = "host:auth", deviceId = _config.DeviceId, hostToken = _config.HostToken });
    }

    private void HandleDisconnected()
    {
        OnConnectedChanged?.Invoke(false);
        if (_reconnecting) return;
        _reconnecting = true;
        _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        OnStatus?.Invoke("Disconnected. Reconnecting...");
        while (true)
        {
            await Task.Delay(3000);
            try
            {
                await ConnectAsync();
                _reconnecting = false;
                return;
            }
            catch
            {
                // keep retrying
            }
        }
    }

    public void RequestNewPairingCode() => _ = _signaling.SendAsync(new { type = "host:pairing-generate" });

    public void RespondToPairingRequest(string sessionRequestId, bool accept) =>
        _ = _signaling.SendAsync(new { type = "host:respond", sessionRequestId, accept });

    public void EndActiveSession()
    {
        if (_activeSessionId is null) return;
        _ = _signaling.SendAsync(new { type = "session:end", sessionId = _activeSessionId });
        TearDownSession("ended_by_host");
    }

    private void HandleMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "host:auth-ok":
                OnConnectedChanged?.Invoke(true);
                _iceServers = ParseIceServers(root);
                OnStatus?.Invoke("Connected.");
                RequestNewPairingCode();
                break;

            case "host:auth-failed":
                OnStatus?.Invoke("This device's credentials were rejected by the server. Re-run setup.");
                break;

            case "host:pairing-code":
                OnPairingCode?.Invoke(root.GetProperty("code").GetString()!, root.GetProperty("expiresAt").GetInt64());
                break;

            case "pairing:incoming-request":
                var reqId = root.GetProperty("sessionRequestId").GetString()!;
                var email = root.GetProperty("controllerEmail").GetString()!;
                _pendingEmailByRequestId[reqId] = email;
                OnPairingRequest?.Invoke(new PendingRequest(reqId, email));
                break;

            case "pairing:request-expired":
                var expiredId = root.GetProperty("sessionRequestId").GetString()!;
                _pendingEmailByRequestId.Remove(expiredId);
                OnPairingRequestExpired?.Invoke(expiredId);
                break;

            case "session:started":
                var sessionId = root.GetProperty("sessionId").GetString()!;
                _activeSessionId = sessionId;
                _ = StartSessionAsync(sessionId);
                break;

            case "signal":
                HandleSignal(root);
                break;

            case "session:ended":
                TearDownSession(root.TryGetProperty("reason", out var r) ? r.GetString() ?? "ended" : "ended");
                break;
        }
    }

    private void HandleSignal(JsonElement root)
    {
        if (_peer is null) return;
        if (root.GetProperty("sessionId").GetString() != _activeSessionId) return;
        var data = root.GetProperty("data");
        if (data.TryGetProperty("sdp", out var sdpEl))
        {
            _ = _peer.HandleRemoteOfferAsync(sdpEl.GetRawText());
        }
        else if (data.TryGetProperty("candidate", out var candEl))
        {
            _peer.HandleRemoteCandidate(candEl.GetRawText());
        }
    }

    private async Task StartSessionAsync(string sessionId)
    {
        var controllerEmail = _pendingEmailByRequestId.Values.LastOrDefault() ?? "controller";

        _peer = new PeerConnectionManager(_iceServers);
        _peer.OnLocalSignal += payload => _ = _signaling.SendAsync(new { type = "signal", sessionId, data = payload });
        _peer.OnConnectionStateChanged += state => OnStatus?.Invoke($"WebRTC: {state}");
        _peer.OnControlMessage += HandleControlMessage;
        await _peer.StartAsync();

        var monitors = ScreenCapture.EnumerateMonitors();
        var mon = monitors.FirstOrDefault(m => m.AdapterIndex == _config.MonitorAdapterIndex && m.OutputIndex == _config.MonitorOutputIndex)
                  ?? monitors.FirstOrDefault()
                  ?? throw new InvalidOperationException("No monitors found to capture");

        _input.TargetMonitorBounds = new Rect
        {
            Left = mon.Left,
            Top = mon.Top,
            Right = mon.Left + mon.Width,
            Bottom = mon.Top + mon.Height,
        };

        _capture = new ScreenCapture(mon.AdapterIndex, mon.OutputIndex);
        _frameClock.Restart();
        _lastFramePushedMs = 0;
        _capture.OnStatus += s => OnStatus?.Invoke(s);
        _capture.OnFrame += frame =>
        {
            long now = _frameClock.ElapsedMilliseconds;
            long delta = now - _lastFramePushedMs;
            if (delta < 66) return; // cap host-side push rate at ~15 fps
            _lastFramePushedMs = now;
            _peer?.PushFrame(frame.Width, frame.Height, frame.Bgra, (uint)Math.Clamp(delta, 1, 1000));
        };
        _capture.Start();

        OnSessionStarted?.Invoke(sessionId, controllerEmail);
    }

    private void HandleControlMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            switch (t)
            {
                case "move":
                    _input.MoveMouseNormalized(root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                    break;
                case "down":
                case "up":
                    _input.MouseButton(t == "down", root.GetProperty("button").GetInt32(),
                        root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                    break;
                case "wheel":
                    _input.MouseWheel(root.GetProperty("dx").GetDouble(), root.GetProperty("dy").GetDouble(),
                        root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                    break;
                case "keydown":
                case "keyup":
                    _input.KeyEvent(t == "keydown", root.GetProperty("key").GetString() ?? "");
                    break;
                case "text":
                    _input.TypeText(root.GetProperty("text").GetString() ?? "");
                    break;
            }
        }
        catch (JsonException)
        {
            // ignore malformed control frames
        }
    }

    private void TearDownSession(string reason)
    {
        _capture?.Stop();
        _capture = null;
        _peer?.Close();
        _peer = null;
        var wasActive = _activeSessionId is not null;
        _activeSessionId = null;
        _pendingEmailByRequestId.Clear();
        if (wasActive)
        {
            OnSessionEnded?.Invoke(reason);
            RequestNewPairingCode();
        }
    }

    private static List<RTCIceServer> ParseIceServers(JsonElement root)
    {
        var list = new List<RTCIceServer>();
        if (!root.TryGetProperty("iceServers", out var arr)) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var server = new RTCIceServer { urls = item.GetProperty("urls").GetString() ?? "" };
            if (item.TryGetProperty("username", out var u)) server.username = u.GetString();
            if (item.TryGetProperty("credential", out var c)) server.credential = c.GetString();
            list.Add(server);
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _capture?.Stop();
        _peer?.Close();
        await _signaling.DisposeAsync();
    }
}
