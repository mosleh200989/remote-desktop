import { useEffect, useRef, useState } from "react";
import { RtcSession } from "../lib/rtc";
import type { PairedSession } from "./PairForm";
import { FileTransferChannel } from "../lib/fileTransfer";
import { FileTransferPanel } from "./FileTransferPanel";

const LONG_PRESS_MS = 500;
const MOVE_CANCEL_PX = 10;
const ZOOM_EPSILON = 0.04;

interface Pt {
  x: number;
  y: number;
}

/**
 * Maps a client-space point to normalized [0,1] video-content coordinates,
 * accounting for object-fit: contain letterboxing and any local zoom/pan
 * transform already baked into the video element's live bounding rect.
 */
function toNormalized(video: HTMLVideoElement, clientX: number, clientY: number): Pt | null {
  const rect = video.getBoundingClientRect();
  const vw = video.videoWidth;
  const vh = video.videoHeight;
  if (!vw || !vh || rect.width === 0 || rect.height === 0) return null;

  const boxAspect = rect.width / rect.height;
  const videoAspect = vw / vh;
  let contentW = rect.width;
  let contentH = rect.height;
  let offsetX = 0;
  let offsetY = 0;
  if (videoAspect > boxAspect) {
    contentH = rect.width / videoAspect;
    offsetY = (rect.height - contentH) / 2;
  } else {
    contentW = rect.height * videoAspect;
    offsetX = (rect.width - contentW) / 2;
  }

  const x = (clientX - rect.left - offsetX) / contentW;
  const y = (clientY - rect.top - offsetY) / contentH;
  if (x < 0 || x > 1 || y < 0 || y > 1) return null;
  return { x, y };
}

export function Viewer({ session, onEnd }: { session: PairedSession; onEnd: () => void }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const keyboardInputRef = useRef<HTMLInputElement>(null);
  const rtcRef = useRef<RtcSession | null>(null);

  const [connState, setConnState] = useState<RTCPeerConnectionState>("new");
  const [dataReady, setDataReady] = useState(false);
  const [view, setView] = useState({ scale: 1, tx: 0, ty: 0 });
  const [fileChannel, setFileChannel] = useState<FileTransferChannel | null>(null);
  const [filesOpen, setFilesOpen] = useState(false);

  useEffect(() => {
    const rtc = new RtcSession(session.signaling, session.sessionId, session.iceServers, {
      onRemoteStream: (stream) => {
        if (videoRef.current) videoRef.current.srcObject = stream;
      },
      onConnectionState: setConnState,
      onDataChannelOpen: () => setDataReady(true),
      onFilesChannelOpen: (dc) => setFileChannel(new FileTransferChannel(dc)),
    });
    rtcRef.current = rtc;
    rtc.start().catch((err) => console.error("Failed to start WebRTC session", err));

    const off = session.signaling.on((msg) => {
      if (msg.type === "session:ended") onEnd();
    });

    return () => {
      off();
      rtc.close();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ---- Pointer gesture engine (mouse + touch, unified) ----
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const active = new Map<number, { start: Pt; last: Pt; startTime: number }>();
    let mode: "none" | "tap-or-drag" | "dragging" | "two-finger" = "none";
    let longPressTimer: ReturnType<typeof setTimeout> | null = null;
    let twoFingerStartDist = 0;
    let twoFingerLastMid: Pt = { x: 0, y: 0 };
    let twoFingerGestureType: "pending" | "zoom" | "scroll" = "pending";

    function send(ev: Parameters<RtcSession["sendControl"]>[0]) {
      rtcRef.current?.sendControl(ev);
    }

    function clearLongPress() {
      if (longPressTimer) {
        clearTimeout(longPressTimer);
        longPressTimer = null;
      }
    }

    function onPointerDown(e: PointerEvent) {
      video!.setPointerCapture(e.pointerId);
      active.set(e.pointerId, { start: { x: e.clientX, y: e.clientY }, last: { x: e.clientX, y: e.clientY }, startTime: performance.now() });

      if (active.size === 1) {
        mode = "tap-or-drag";
        clearLongPress();
        longPressTimer = setTimeout(() => {
          const p = toNormalized(video!, e.clientX, e.clientY);
          if (p && mode === "tap-or-drag") {
            send({ t: "down", x: p.x, y: p.y, button: 2 });
            send({ t: "up", x: p.x, y: p.y, button: 2 });
            mode = "none";
          }
        }, LONG_PRESS_MS);
      } else if (active.size === 2) {
        clearLongPress();
        if (mode === "dragging") {
          const p = firstNormalized();
          if (p) send({ t: "up", x: p.x, y: p.y, button: (e.button as 0 | 1 | 2) ?? 0 });
        }
        mode = "two-finger";
        twoFingerGestureType = "pending";
        const pts = [...active.values()];
        twoFingerStartDist = dist(pts[0].last, pts[1].last);
        twoFingerLastMid = mid(pts[0].last, pts[1].last);
      }
    }

    function firstNormalized(): Pt | null {
      const first = [...active.values()][0];
      return first ? toNormalized(video!, first.last.x, first.last.y) : null;
    }

    function onPointerMove(e: PointerEvent) {
      const entry = active.get(e.pointerId);
      if (!entry) return;
      entry.last = { x: e.clientX, y: e.clientY };

      if (mode === "tap-or-drag" && active.size === 1) {
        const moved = dist(entry.start, entry.last);
        if (moved > MOVE_CANCEL_PX) {
          clearLongPress();
          mode = "dragging";
          const p = toNormalized(video!, entry.start.x, entry.start.y);
          if (p) send({ t: "down", x: p.x, y: p.y, button: 0 });
        }
      }
      if (mode === "dragging") {
        const p = toNormalized(video!, entry.last.x, entry.last.y);
        if (p) send({ t: "move", x: p.x, y: p.y });
      } else if (mode === "tap-or-drag" && video!.matches(":hover") && !("ontouchstart" in window)) {
        // Desktop mouse: forward hover movement even without a button held.
        const p = toNormalized(video!, entry.last.x, entry.last.y);
        if (p) send({ t: "move", x: p.x, y: p.y });
      } else if (mode === "two-finger" && active.size === 2) {
        const pts = [...active.values()];
        const d = dist(pts[0].last, pts[1].last);
        const m = mid(pts[0].last, pts[1].last);
        const ratio = d / (twoFingerStartDist || 1);

        if (twoFingerGestureType === "pending") {
          if (Math.abs(ratio - 1) > ZOOM_EPSILON) twoFingerGestureType = "zoom";
          else if (dist(twoFingerLastMid, m) > MOVE_CANCEL_PX) twoFingerGestureType = "scroll";
        }

        if (twoFingerGestureType === "zoom") {
          setView((v) => ({ ...v, scale: clamp(v.scale * (d / (twoFingerStartDist || d)), 1, 4) }));
          twoFingerStartDist = d;
        } else if (twoFingerGestureType === "scroll") {
          const dx = m.x - twoFingerLastMid.x;
          const dy = m.y - twoFingerLastMid.y;
          const p = toNormalized(video!, m.x, m.y);
          if (p) send({ t: "wheel", x: p.x, y: p.y, dx: -dx, dy: -dy });
        }
        twoFingerLastMid = m;
      }
    }

    function onPointerUp(e: PointerEvent) {
      const entry = active.get(e.pointerId);
      active.delete(e.pointerId);
      clearLongPress();

      if (mode === "tap-or-drag" && entry) {
        const p = toNormalized(video!, entry.last.x, entry.last.y);
        if (p) {
          const button = e.button === 2 ? 2 : e.button === 1 ? 1 : 0;
          send({ t: "down", x: p.x, y: p.y, button });
          send({ t: "up", x: p.x, y: p.y, button });
        }
        mode = "none";
      } else if (mode === "dragging" && entry) {
        const p = toNormalized(video!, entry.last.x, entry.last.y);
        if (p) send({ t: "up", x: p.x, y: p.y, button: 0 });
        mode = active.size > 0 ? "tap-or-drag" : "none";
      } else if (mode === "two-finger") {
        if (active.size < 2) mode = active.size === 1 ? "tap-or-drag" : "none";
      }
    }

    video.addEventListener("pointerdown", onPointerDown);
    video.addEventListener("pointermove", onPointerMove);
    video.addEventListener("pointerup", onPointerUp);
    video.addEventListener("pointercancel", onPointerUp);
    video.addEventListener("contextmenu", (e) => e.preventDefault());
    // Desktop wheel scrolling forwards directly (no pinch involved).
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const p = toNormalized(video!, e.clientX, e.clientY);
      if (p) send({ t: "wheel", x: p.x, y: p.y, dx: e.deltaX, dy: e.deltaY });
    };
    video.addEventListener("wheel", onWheel, { passive: false });

    return () => {
      video.removeEventListener("pointerdown", onPointerDown);
      video.removeEventListener("pointermove", onPointerMove);
      video.removeEventListener("pointerup", onPointerUp);
      video.removeEventListener("pointercancel", onPointerUp);
      video.removeEventListener("wheel", onWheel);
      clearLongPress();
    };
  }, []);

  // ---- On-screen keyboard: hidden input captures real key events for
  // control keys, and text-composition events for arbitrary character input
  // (needed because mobile virtual keyboards rarely fire meaningful keydown
  // events for printable characters). ----
  function openKeyboard() {
    keyboardInputRef.current?.focus();
  }

  function onKeyboardKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    const controlKeys = [
      "Enter", "Backspace", "Tab", "Escape", "Delete",
      "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
      "Home", "End", "PageUp", "PageDown",
    ];
    if (controlKeys.includes(e.key) || e.ctrlKey || e.altKey || e.metaKey) {
      rtcRef.current?.sendControl({
        t: "keydown",
        key: e.key,
        code: e.code,
        ctrl: e.ctrlKey,
        shift: e.shiftKey,
        alt: e.altKey,
        meta: e.metaKey,
      });
      if (e.key.length > 1 || e.ctrlKey || e.altKey || e.metaKey) {
        // Prevent the hidden input from also inserting/echoing the character.
        e.preventDefault();
      }
    }
  }

  function onKeyboardKeyUp(e: React.KeyboardEvent<HTMLInputElement>) {
    rtcRef.current?.sendControl({
      t: "keyup",
      key: e.key,
      code: e.code,
      ctrl: e.ctrlKey,
      shift: e.shiftKey,
      alt: e.altKey,
      meta: e.metaKey,
    });
  }

  function onBeforeInput(e: React.FormEvent<HTMLInputElement> & { nativeEvent: InputEvent }) {
    const native = e.nativeEvent;
    if (native.data) {
      rtcRef.current?.sendControl({ t: "text", text: native.data });
    }
    // Keep the hidden field empty so it never grows unbounded.
    requestAnimationFrame(() => {
      if (keyboardInputRef.current) keyboardInputRef.current.value = "";
    });
  }

  async function endSession() {
    await rtcRef.current?.hangUp();
    onEnd();
  }

  return (
    <div className="viewer">
      <div className="viewer-toolbar">
        <span className={`status-dot ${connState}`} />
        <span className="status-text">{describeConnState(connState, dataReady)}</span>
        <div className="spacer" />
        <button onClick={openKeyboard} title="Show keyboard">
          ⌨ Keyboard
        </button>
        <button onClick={() => setFilesOpen((v) => !v)} title="File transfer">
          📁 Files
        </button>
        <button onClick={() => setView({ scale: 1, tx: 0, ty: 0 })} title="Reset zoom">
          ⤢ Reset view
        </button>
        <button className="danger" onClick={endSession}>
          End session
        </button>
      </div>
      <div className="viewer-stage">
        <video
          ref={videoRef}
          autoPlay
          playsInline
          muted
          style={{ transform: `translate(${view.tx}px, ${view.ty}px) scale(${view.scale})` }}
        />
        {filesOpen && <FileTransferPanel channel={fileChannel} onClose={() => setFilesOpen(false)} />}
      </div>
      <input
        ref={keyboardInputRef}
        className="hidden-keyboard-input"
        onKeyDown={onKeyboardKeyDown}
        onKeyUp={onKeyboardKeyUp}
        onBeforeInput={onBeforeInput as any}
        autoComplete="off"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
      />
    </div>
  );
}

function dist(a: Pt, b: Pt) {
  return Math.hypot(a.x - b.x, a.y - b.y);
}
function mid(a: Pt, b: Pt): Pt {
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}
function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}

function describeConnState(state: RTCPeerConnectionState, dataReady: boolean): string {
  if (state === "connected" && dataReady) return "Connected";
  if (state === "connected") return "Connected (waiting for control channel)";
  if (state === "connecting" || state === "new") return "Connecting...";
  if (state === "disconnected") return "Reconnecting...";
  if (state === "failed") return "Connection failed";
  if (state === "closed") return "Session ended";
  return state;
}
