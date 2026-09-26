using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace RemoteHost.Rtc;

/// <summary>
/// The controller side of a session, used when this same app connects out to
/// another RemoteHost install: always the WebRTC offerer, creates the
/// "control" and "files" data channels itself, and decodes the incoming VP8
/// video track (via the same VideoEncoderEndPoint class the host uses to
/// encode, here used purely as a decoder/sink).
/// </summary>
public sealed class ControllerPeerConnection : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly VideoEncoderEndPoint _videoSink;
    private RTCDataChannel? _controlChannel;

    public event Action<JsonObject>? OnLocalSignal;
    public event Action<byte[], int, int, int, VideoPixelFormatsEnum>? OnDecodedFrame;
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
    public event Action? OnControlChannelOpen;
    public event Action<FileTransferChannel>? OnFilesChannelReady;

    public ControllerPeerConnection(List<RTCIceServer> iceServers)
    {
        var config = new RTCConfiguration { iceServers = iceServers };
        _pc = new RTCPeerConnection(config);

        var vp8Format = new VideoFormat(VideoCodecsEnum.VP8, 96, 90000);
        _videoSink = new VideoEncoderEndPoint();
        _videoSink.SetVideoSinkFormat(vp8Format);
        var track = new MediaStreamTrack(new List<VideoFormat> { vp8Format }, MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(track);

        _pc.OnVideoFrameReceived += (remoteEp, timestamp, frame, format) => _videoSink.GotVideoFrame(remoteEp, timestamp, frame, format);
        _videoSink.OnVideoSinkDecodedSample += (sample, width, height, stride, pixelFormat) =>
            OnDecodedFrame?.Invoke(sample, (int)width, (int)height, stride, pixelFormat);

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            var node = JsonNode.Parse(candidate.toJSON());
            OnLocalSignal?.Invoke(new JsonObject { ["candidate"] = node });
        };
        _pc.onconnectionstatechange += state => OnConnectionStateChanged?.Invoke(state);
    }

    /// <summary>Creates both data channels, then the offer, and raises OnLocalSignal with it.</summary>
    public async Task StartAsync()
    {
        _controlChannel = await _pc.createDataChannel("control", new RTCDataChannelInit());
        _controlChannel.onopen += () => OnControlChannelOpen?.Invoke();

        var filesDc = await _pc.createDataChannel("files", new RTCDataChannelInit());
        OnFilesChannelReady?.Invoke(new FileTransferChannel(filesDc));

        var offer = _pc.createOffer(new RTCOfferOptions());
        await _pc.setLocalDescription(offer);
        OnLocalSignal?.Invoke(new JsonObject { ["sdp"] = JsonNode.Parse(offer.toJSON()) });
    }

    public void SendControl(object ev)
    {
        if (_controlChannel?.readyState == RTCDataChannelState.open)
        {
            _controlChannel.send(JsonSerializer.Serialize(ev));
        }
    }

    public void HandleRemoteAnswer(string sdpJson)
    {
        if (RTCSessionDescriptionInit.TryParse(sdpJson, out var init))
        {
            _pc.setRemoteDescription(init);
        }
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
        _videoSink.Dispose();
    }

    public void Dispose() => Close();
}
