import type { IceServerConfig } from "./signaling";
import { SignalingSocket } from "./signaling";

export type ControlEvent =
  | { t: "move"; x: number; y: number }
  | { t: "down" | "up"; x: number; y: number; button: 0 | 1 | 2 }
  | { t: "wheel"; x: number; y: number; dx: number; dy: number }
  | { t: "keydown" | "keyup"; key: string; code: string; ctrl: boolean; shift: boolean; alt: boolean; meta: boolean }
  | { t: "text"; text: string };

interface RtcSessionCallbacks {
  onRemoteStream: (stream: MediaStream) => void;
  onConnectionState: (state: RTCPeerConnectionState) => void;
  onDataChannelOpen: () => void;
}

/**
 * The browser controller is always the WebRTC offerer: it creates the peer
 * connection, opens the "control" data channel, requests a recv-only video
 * track, and sends the offer down the signaling channel. The Windows host
 * answers. ICE candidates trickle both ways over the same signaling channel.
 */
export class RtcSession {
  readonly pc: RTCPeerConnection;
  private dataChannel: RTCDataChannel;
  private unsubscribe: () => void;
  private signaling: SignalingSocket;
  private sessionId: string;

  constructor(
    signaling: SignalingSocket,
    sessionId: string,
    iceServers: IceServerConfig[],
    callbacks: RtcSessionCallbacks
  ) {
    this.signaling = signaling;
    this.sessionId = sessionId;
    this.pc = new RTCPeerConnection({ iceServers: iceServers as RTCIceServer[] });
    this.pc.addTransceiver("video", { direction: "recvonly" });

    this.dataChannel = this.pc.createDataChannel("control", { ordered: true });
    this.dataChannel.onopen = () => callbacks.onDataChannelOpen();

    this.pc.ontrack = (ev) => {
      if (ev.streams[0]) callbacks.onRemoteStream(ev.streams[0]);
    };
    this.pc.onconnectionstatechange = () => callbacks.onConnectionState(this.pc.connectionState);
    this.pc.onicecandidate = (ev) => {
      if (ev.candidate) {
        this.signaling.send({
          type: "signal",
          sessionId,
          data: { candidate: ev.candidate.toJSON() },
        });
      }
    };

    this.unsubscribe = signaling.on((msg) => {
      if (msg.type !== "signal" || msg.sessionId !== sessionId) return;
      this.handleSignal(msg.data);
    });
  }

  private async handleSignal(data: any) {
    if (data.sdp) {
      await this.pc.setRemoteDescription(new RTCSessionDescription(data.sdp));
    } else if (data.candidate) {
      try {
        await this.pc.addIceCandidate(new RTCIceCandidate(data.candidate));
      } catch (err) {
        console.warn("Failed to add ICE candidate", err);
      }
    }
  }

  async start() {
    const offer = await this.pc.createOffer();
    await this.pc.setLocalDescription(offer);
    await this.signaling.send({
      type: "signal",
      sessionId: this.sessionId,
      data: { sdp: this.pc.localDescription },
    });
  }

  sendControl(ev: ControlEvent) {
    if (this.dataChannel.readyState === "open") {
      this.dataChannel.send(JSON.stringify(ev));
    }
  }

  /**
   * Local-only teardown: closes the peer connection but does NOT tell the
   * server to end the session. Safe to call from a React effect cleanup
   * (including React StrictMode's dev-mode double-invoke) or after the
   * server already told us the session ended. Use `hangUp()` when the user
   * is the one deliberately ending a live session.
   */
  close() {
    this.unsubscribe();
    this.dataChannel.close();
    this.pc.close();
  }

  /** User-initiated end: notifies the server, then tears down locally. */
  async hangUp() {
    await this.signaling.send({ type: "session:end", sessionId: this.sessionId }).catch(() => {});
    this.close();
  }
}
