using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace RemoteHost.Rtc;

/// <summary>
/// Wraps a single WebRTC session on the host side: the host is always the
/// WebRTC answerer. Owns the VP8 video encoder pipeline and the inbound
/// "control" data channel created by the browser controller.
/// </summary>
public sealed class PeerConnectionManager : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly VideoEncoderEndPoint _videoEndPoint;
    private RTCDataChannel? _dataChannel;

    /// <summary>Raised with a JSON object shaped like {"sdp":{...}} or {"candidate":{...}} to relay to the signaling server.</summary>
    public event Action<JsonObject>? OnLocalSignal;
    public event Action<string>? OnControlMessage;
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
    public event Action? OnDataChannelOpen;

    public PeerConnectionManager(List<RTCIceServer> iceServers)
    {
        var config = new RTCConfiguration { iceServers = iceServers };
        _pc = new RTCPeerConnection(config);

        // VideoEncoderEndPoint's own default format list is not VP8, so
        // browsers never negotiate a match against it. Declare VP8
        // ourselves (matching the codec VpxVideoEncoder actually produces)
        // and tell the endpoint to use it explicitly.
        var vp8Format = new VideoFormat(VideoCodecsEnum.VP8, 96, 90000);
        _videoEndPoint = new VideoEncoderEndPoint();
        _videoEndPoint.RestrictFormats(f => f.Codec == VideoCodecsEnum.VP8);
        _videoEndPoint.SetVideoSourceFormat(vp8Format);
        var track = new MediaStreamTrack(new List<VideoFormat> { vp8Format }, MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(track);
        _videoEndPoint.OnVideoSourceEncodedSample += (durationRtpUnits, sample) => _pc.SendVideo(durationRtpUnits, sample);

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            var node = JsonNode.Parse(candidate.toJSON());
            OnLocalSignal?.Invoke(new JsonObject { ["candidate"] = node });
        };
        _pc.onconnectionstatechange += state => OnConnectionStateChanged?.Invoke(state);
        _pc.ondatachannel += dc =>
        {
            _dataChannel = dc;
            dc.onmessage += (_, _, data) => OnControlMessage?.Invoke(Encoding.UTF8.GetString(data));
            dc.onopen += () => OnDataChannelOpen?.Invoke();
        };
    }

    public Task StartAsync() => _videoEndPoint.StartVideo();

    public void PushFrame(int width, int height, byte[] bgra, uint durationMilliseconds)
    {
        // The RTP video stream only has a negotiated sending format once the
        // SDP offer/answer exchange and DTLS handshake have both completed;
        // pushing samples before that throws deep inside SIPSorcery.
        if (_pc.connectionState != RTCPeerConnectionState.connected) return;
        if (!_videoEndPoint.HasEncodedVideoSubscribers()) return;
        _videoEndPoint.ExternalVideoSourceRawSample(durationMilliseconds, width, height, bgra, VideoPixelFormatsEnum.Bgra);
    }

    public async Task HandleRemoteOfferAsync(string offerJson)
    {
        if (!RTCSessionDescriptionInit.TryParse(offerJson, out var init))
            throw new InvalidOperationException("Could not parse remote SDP offer");
        var setResult = _pc.setRemoteDescription(init);
        if (setResult != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        var answer = _pc.createAnswer(new RTCAnswerOptions());
        await _pc.setLocalDescription(answer);
        OnLocalSignal?.Invoke(new JsonObject { ["sdp"] = JsonNode.Parse(answer.toJSON()) });
    }

    public void HandleRemoteCandidate(string candidateJson)
    {
        if (RTCIceCandidateInit.TryParse(candidateJson, out var init))
        {
            _pc.addIceCandidate(init);
        }
    }

    public void Close()
    {
        _pc.close();
        _videoEndPoint.Dispose();
    }

    public void Dispose() => Close();
}
