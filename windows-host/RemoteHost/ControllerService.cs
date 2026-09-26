using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RemoteHost.Rtc;
using RemoteHost.Signaling;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemoteHost;

/// <summary>
/// Drives a single outbound session: this install connects to the signaling
/// server anonymously (just a display name, no account), attempts to pair
/// with another device's ID + code, and - once the host accepts - becomes
/// the WebRTC offerer, decoding the incoming video and forwarding local
/// input as control messages.
/// </summary>
public sealed class ControllerService : IAsyncDisposable, IFileTransferSession
{
    private readonly SignalingClient _signaling = new();
    private ControllerPeerConnection? _peer;
    private FileTransferChannel? _files;
    private string? _sessionId;
    private List<RTCIceServer> _iceServers = new();
    private TaskCompletionSource<bool>? _helloTcs;

    public event Action<string>? OnStatus;
    public event Action? OnPairingPending;
    public event Action<string>? OnRejected; // reason
    public event Action? OnSessionStarted;
    public event Action<string>? OnSessionEnded; // reason
    public event Action<byte[], int, int, int, VideoPixelFormatsEnum>? OnDecodedFrame;
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    public event Action<IncomingFileOffer>? OnIncomingFileOffer;
    public event Action<string, long, long>? OnFileProgress;
    public event Action<string, string>? OnFileReceiveComplete;
    public event Action<string>? OnFileSendComplete;
    public event Action<string, bool>? OnFileCancelled;

    /// <summary>Connects, says hello, and immediately requests pairing with deviceId+code.</summary>
    public async Task ConnectAndPairAsync(string wsUrl, string displayName, string deviceId, string code)
    {
        _signaling.OnMessage += HandleMessage;
        _helloTcs = new TaskCompletionSource<bool>();

        OnStatus?.Invoke("Connecting to signaling server...");
        await _signaling.ConnectAsync(wsUrl);
        await _signaling.SendAsync(new { type = "controller:hello", displayName });
        await _helloTcs.Task;

        OnStatus?.Invoke("Requesting access...");
        await _signaling.SendAsync(new { type = "pairing:attempt", deviceId, code });
    }

    private void HandleMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "controller:hello-ok":
                _iceServers = IceServerParser.Parse(root);
                _helloTcs?.TrySetResult(true);
                break;

            case "pairing:pending":
                OnPairingPending?.Invoke();
                break;

            case "pairing:rejected":
                OnRejected?.Invoke(root.TryGetProperty("reason", out var r) ? r.GetString() ?? "unknown" : "unknown");
                break;

            case "session:started":
                _sessionId = root.GetProperty("sessionId").GetString();
                _ = StartPeerAsync();
                break;

            case "signal":
                HandleSignal(root);
                break;

            case "session:ended":
                _sessionId = null;
                OnSessionEnded?.Invoke(root.TryGetProperty("reason", out var er) ? er.GetString() ?? "ended" : "ended");
                break;
        }
    }

    private async Task StartPeerAsync()
    {
        _peer = new ControllerPeerConnection(_iceServers);
        _peer.OnLocalSignal += payload => _ = _signaling.SendAsync(new { type = "signal", sessionId = _sessionId, data = payload });
        _peer.OnDecodedFrame += (sample, w, h, stride, pf) => OnDecodedFrame?.Invoke(sample, w, h, stride, pf);
        _peer.OnConnectionStateChanged += s => OnConnectionStateChanged?.Invoke(s);
        _peer.OnFilesChannelReady += f =>
        {
            _files = f;
            f.OnIncomingOffer += o => OnIncomingFileOffer?.Invoke(o);
            f.OnProgress += (id, done, total) => OnFileProgress?.Invoke(id, done, total);
            f.OnReceiveComplete += (id, path) => OnFileReceiveComplete?.Invoke(id, path);
            f.OnSendComplete += id => OnFileSendComplete?.Invoke(id);
            f.OnCancelled += (id, remote) => OnFileCancelled?.Invoke(id, remote);
        };
        await _peer.StartAsync();
        OnSessionStarted?.Invoke();
    }

    public string? OfferFile(string filePath) => _files?.OfferFile(filePath);
    public void AcceptFileOffer(string id, string savePath, long size) => _files?.AcceptOffer(id, savePath, size);
    public void RejectFileOffer(string id) => _files?.RejectOffer(id);
    public void CancelFileTransfer(string id) => _files?.Cancel(id);

    private void HandleSignal(JsonElement root)
    {
        if (_peer is null) return;
        if (root.GetProperty("sessionId").GetString() != _sessionId) return;
        var data = root.GetProperty("data");
        if (data.TryGetProperty("sdp", out var sdpEl)) _peer.HandleRemoteAnswer(sdpEl.GetRawText());
        else if (data.TryGetProperty("candidate", out var candEl)) _peer.HandleRemoteCandidate(candEl.GetRawText());
    }

    public void SendControl(object ev) => _peer?.SendControl(ev);

    public void EndSession()
    {
        if (_sessionId != null)
        {
            _ = _signaling.SendAsync(new { type = "session:end", sessionId = _sessionId });
        }
        _peer?.Close();
        _peer = null;
    }

    public async ValueTask DisposeAsync()
    {
        _peer?.Close();
        await _signaling.DisposeAsync();
    }
}
