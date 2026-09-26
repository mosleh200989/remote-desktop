import { randomUUID } from "node:crypto";
import { IncomingMessage } from "node:http";
import { WebSocket, WebSocketServer } from "ws";
import { db } from "../db";
import { env } from "../env";
import { verifyHostToken } from "../auth";
import {
  checkAndConsumePairingCode,
  createPairingCode,
  getDeviceByDeviceId,
  touchDeviceLastSeen,
} from "../pairing";

type HostConn = {
  kind: "host";
  ws: WebSocket;
  deviceId: string;
  activeSessionId: string | null;
};

type ControllerConn = {
  kind: "controller";
  ws: WebSocket;
  // No accounts: this is just whatever name the controller typed in before
  // connecting (e.g. "Sara's laptop"), shown to the host in the approval
  // prompt. It is not verified or unique.
  displayName: string;
  sessionId: string | null;
};

type Conn = HostConn | ControllerConn;

interface PendingRequest {
  sessionRequestId: string;
  deviceId: string;
  controller: ControllerConn;
  requestedAt: number;
}

interface ActiveSession {
  sessionId: string;
  deviceId: string;
  host: HostConn;
  controller: ControllerConn;
}

const AUTH_TIMEOUT_MS = 10_000;

export function attachSignalingHub(wss: WebSocketServer) {
  const hostsByDeviceId = new Map<string, HostConn>();
  const pendingRequests = new Map<string, PendingRequest>();
  const activeSessions = new Map<string, ActiveSession>();
  const connMeta = new WeakMap<WebSocket, Conn>();

  function send(ws: WebSocket, msg: unknown) {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  }

  function endSession(sessionId: string, reason: string) {
    const session = activeSessions.get(sessionId);
    if (!session) return;
    activeSessions.delete(sessionId);
    session.host.activeSessionId = null;
    session.controller.sessionId = null;
    send(session.host.ws, { type: "session:ended", sessionId, reason });
    send(session.controller.ws, { type: "session:ended", sessionId, reason });
    db.prepare("UPDATE sessions SET status = 'ended', ended_at = ? WHERE id = ?").run(
      Date.now(),
      sessionId
    );
  }

  function rejectPending(sessionRequestId: string, reason: string) {
    const pending = pendingRequests.get(sessionRequestId);
    if (!pending) return;
    pendingRequests.delete(sessionRequestId);
    send(pending.controller.ws, { type: "pairing:rejected", reason });
  }

  wss.on("connection", (ws: WebSocket, req: IncomingMessage) => {
    let authed = false;
    const timeout = setTimeout(() => {
      if (!authed) ws.close(4001, "auth timeout");
    }, AUTH_TIMEOUT_MS);

    ws.on("message", async (raw) => {
      let msg: any;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        return send(ws, { type: "error", reason: "invalid_json" });
      }

      const existing = connMeta.get(ws);

      // ---- Authentication (first message on the socket) ----
      if (!existing) {
        if (msg.type === "host:auth") {
          const device = getDeviceByDeviceId(String(msg.deviceId ?? ""));
          const ok =
            device && (await verifyHostToken(String(msg.hostToken ?? ""), device.host_token_hash));
          if (!ok || !device) {
            send(ws, { type: "host:auth-failed", reason: "invalid_credentials" });
            return ws.close(4003, "auth failed");
          }
          authed = true;
          clearTimeout(timeout);
          // Only one live connection per device at a time.
          const prior = hostsByDeviceId.get(device.device_id);
          if (prior) prior.ws.close(4009, "replaced by new connection");
          const hostConn: HostConn = {
            kind: "host",
            ws,
            deviceId: device.device_id,
            activeSessionId: null,
          };
          connMeta.set(ws, hostConn);
          hostsByDeviceId.set(device.device_id, hostConn);
          touchDeviceLastSeen(device.device_id);
          send(ws, { type: "host:auth-ok", deviceId: device.device_id, iceServers: env.iceServers });
          return;
        }

        if (msg.type === "controller:hello") {
          const displayName = String(msg.displayName ?? "").trim().slice(0, 64) || "Someone";
          authed = true;
          clearTimeout(timeout);
          const controllerConn: ControllerConn = {
            kind: "controller",
            ws,
            displayName,
            sessionId: null,
          };
          connMeta.set(ws, controllerConn);
          send(ws, { type: "controller:hello-ok", iceServers: env.iceServers });
          return;
        }

        send(ws, { type: "error", reason: "must_authenticate_first" });
        return ws.close(4001, "must authenticate first");
      }

      // ---- Authenticated message handling ----
      if (existing.kind === "host") {
        const host = existing;

        if (msg.type === "host:pairing-generate") {
          const { code, expiresAt } = createPairingCode(host.deviceId);
          return send(ws, { type: "host:pairing-code", code, expiresAt });
        }

        if (msg.type === "host:respond") {
          const pending = pendingRequests.get(String(msg.sessionRequestId ?? ""));
          if (!pending || pending.deviceId !== host.deviceId) return;
          pendingRequests.delete(pending.sessionRequestId);

          if (!msg.accept) {
            send(pending.controller.ws, { type: "pairing:rejected", reason: "rejected_by_host" });
            return;
          }
          if (host.activeSessionId) {
            // Someone else grabbed the single controller slot while this was pending.
            send(pending.controller.ws, { type: "pairing:rejected", reason: "device_busy" });
            return;
          }

          const sessionId = randomUUID();
          const session: ActiveSession = {
            sessionId,
            deviceId: host.deviceId,
            host,
            controller: pending.controller,
          };
          activeSessions.set(sessionId, session);
          host.activeSessionId = sessionId;
          pending.controller.sessionId = sessionId;

          db.prepare(
            "INSERT INTO sessions (id, device_id, controller_name, status, created_at) VALUES (?, ?, ?, 'active', ?)"
          ).run(sessionId, host.deviceId, pending.controller.displayName, Date.now());

          send(host.ws, { type: "session:started", sessionId, role: "host" });
          send(pending.controller.ws, {
            type: "session:started",
            sessionId,
            role: "controller",
            iceServers: env.iceServers,
          });
          return;
        }

        if (msg.type === "signal" || msg.type === "session:end") {
          const sessionId = String(msg.sessionId ?? "");
          const session = activeSessions.get(sessionId);
          if (!session || session.host !== host) return; // not your session
          if (msg.type === "session:end") return endSession(sessionId, "ended_by_host");
          send(session.controller.ws, { type: "signal", sessionId, data: msg.data });
          return;
        }
      }

      if (existing.kind === "controller") {
        const controller = existing;

        if (msg.type === "pairing:attempt") {
          const deviceId = String(msg.deviceId ?? "").trim();
          const code = String(msg.code ?? "").trim();
          const host = hostsByDeviceId.get(deviceId);
          if (!host) {
            return send(ws, { type: "pairing:rejected", reason: "host_offline" });
          }
          if (host.activeSessionId) {
            return send(ws, { type: "pairing:rejected", reason: "device_busy" });
          }
          const result = checkAndConsumePairingCode(deviceId, code);
          if (!result.ok) {
            return send(ws, { type: "pairing:rejected", reason: result.reason });
          }
          const sessionRequestId = randomUUID();
          pendingRequests.set(sessionRequestId, {
            sessionRequestId,
            deviceId,
            controller,
            requestedAt: Date.now(),
          });
          send(ws, { type: "pairing:pending", sessionRequestId });
          send(host.ws, {
            type: "pairing:incoming-request",
            sessionRequestId,
            controllerName: controller.displayName,
            requestedAt: Date.now(),
          });
          // Auto-expire the approval prompt so a host can't leave a stale
          // request sitting around forever.
          setTimeout(() => {
            if (pendingRequests.has(sessionRequestId)) {
              rejectPending(sessionRequestId, "timed_out");
              send(host.ws, { type: "pairing:request-expired", sessionRequestId });
            }
          }, env.pairingCodeTtlSeconds * 1000);
          return;
        }

        if (msg.type === "signal" || msg.type === "session:end") {
          const sessionId = String(msg.sessionId ?? "");
          const session = activeSessions.get(sessionId);
          if (!session || session.controller !== controller) return; // not your session
          if (msg.type === "session:end") return endSession(sessionId, "ended_by_controller");
          send(session.host.ws, { type: "signal", sessionId, data: msg.data });
          return;
        }
      }
    });

    ws.on("close", () => {
      clearTimeout(timeout);
      const conn = connMeta.get(ws);
      if (!conn) return;
      if (conn.kind === "host") {
        hostsByDeviceId.delete(conn.deviceId);
        if (conn.activeSessionId) endSession(conn.activeSessionId, "host_disconnected");
        for (const [id, p] of pendingRequests) {
          if (p.deviceId === conn.deviceId) rejectPending(id, "host_disconnected");
        }
      } else {
        if (conn.sessionId) endSession(conn.sessionId, "controller_disconnected");
        for (const [id, p] of pendingRequests) {
          if (p.controller === conn) pendingRequests.delete(id);
        }
      }
    });
  });
}
