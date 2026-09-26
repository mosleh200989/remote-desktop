import type { FormEvent } from "react";
import { useState } from "react";
import { SignalingSocket } from "../lib/signaling";
import type { IceServerConfig } from "../lib/signaling";

export interface PairedSession {
  signaling: SignalingSocket;
  sessionId: string;
  iceServers: IceServerConfig[];
}

const NAME_KEY = "rd_display_name";

export function PairForm({ onPaired }: { onPaired: (session: PairedSession) => void }) {
  const [displayName, setDisplayName] = useState(() => localStorage.getItem(NAME_KEY) ?? "");
  const [deviceId, setDeviceId] = useState("");
  const [code, setCode] = useState("");
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    localStorage.setItem(NAME_KEY, displayName);
    setBusy(true);
    setStatus("Connecting to signaling server...");
    const signaling = new SignalingSocket(displayName.trim() || "Someone");
    try {
      const { iceServers } = await signaling.authenticate();
      setStatus("Requesting access from the host...");
      await signaling.send({
        type: "pairing:attempt",
        deviceId: deviceId.replace(/\s+/g, ""),
        code: code.trim(),
      });
      const pendingOrRejected = await signaling.waitFor(
        (m) => m.type === "pairing:pending" || m.type === "pairing:rejected"
      );
      if (pendingOrRejected.type === "pairing:rejected") {
        throw new Error(describeRejection(pendingOrRejected.reason));
      }
      setStatus("Waiting for the host to approve this session...");
      const outcome = await signaling.waitFor(
        (m) => m.type === "session:started" || m.type === "pairing:rejected",
        180_000
      );
      if (outcome.type === "pairing:rejected") {
        throw new Error(describeRejection(outcome.reason));
      }
      onPaired({
        signaling,
        sessionId: outcome.sessionId,
        iceServers: outcome.iceServers ?? iceServers,
      });
    } catch (err: any) {
      setStatus(null);
      setBusy(false);
      signaling.close();
      alert(err.message ?? "Pairing failed");
    }
  }

  return (
    <div className="card">
      <h1>Remote Desktop</h1>
      <p className="subtitle">Enter the device ID and pairing code shown on the Windows host.</p>
      <form onSubmit={submit}>
        <label>
          Your name (shown to the host)
          <input
            required
            placeholder="e.g. Sara's laptop"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            disabled={busy}
            maxLength={64}
          />
        </label>
        <label>
          Device ID
          <input
            required
            inputMode="numeric"
            placeholder="482 917 305"
            value={deviceId}
            onChange={(e) => setDeviceId(e.target.value)}
            disabled={busy}
          />
        </label>
        <label>
          Pairing code
          <input
            required
            placeholder="8-character code"
            value={code}
            onChange={(e) => setCode(e.target.value.toUpperCase())}
            disabled={busy}
            maxLength={8}
            style={{ letterSpacing: "0.2em", textTransform: "uppercase" }}
          />
        </label>
        <button type="submit" disabled={busy}>
          {busy ? "Connecting..." : "Request access"}
        </button>
      </form>
      {status && <div className="status">{status}</div>}
    </div>
  );
}

function describeRejection(reason: string): string {
  switch (reason) {
    case "host_offline":
      return "That device isn't online right now.";
    case "device_busy":
      return "That device already has an active controller session.";
    case "not_found":
      return "That code doesn't match this device. Double-check both and try again.";
    case "already_used":
      return "That code has already been used. Ask the host to generate a new one.";
    case "expired":
      return "That code has expired. Ask the host to generate a new one.";
    case "locked_out":
      return "Too many attempts for this device. Please wait a few minutes.";
    case "rejected_by_host":
      return "The host rejected the connection request.";
    case "timed_out":
      return "The host didn't respond in time.";
    default:
      return `Pairing failed (${reason}).`;
  }
}
