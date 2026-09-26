/**
 * Self-contained end-to-end smoke test: boots the real server on a throwaway
 * port + throwaway DB file, then drives it exactly like the Windows host and
 * the web controller would - register a device anonymously, pair, approve,
 * exchange a fake SDP blob, end the session. Exits non-zero on any assertion
 * failure.
 */
import assert from "node:assert/strict";
import { createServer } from "node:http";
import fs from "node:fs";
import express from "express";
import { WebSocketServer, WebSocket } from "ws";

process.env.DB_PATH = `./data/e2e-test-${Date.now()}.db`;
process.env.JWT_SECRET = "test-secret-not-for-production";
process.env.PAIRING_CODE_TTL_SECONDS = "60";

async function main() {
  const { attachSignalingHub } = await import("../ws/hub");
  const { devicesRouter } = await import("../routes/devices");
  const { db } = await import("../db");

  const app = express();
  app.use(express.json());
  app.use("/api/devices", devicesRouter);
  const server = createServer(app);
  const wss = new WebSocketServer({ server, path: "/ws" });
  attachSignalingHub(wss);

  await new Promise<void>((resolve) => server.listen(0, resolve));
  const port = (server.address() as any).port;
  const base = `http://127.0.0.1:${port}`;
  const wsBase = `ws://127.0.0.1:${port}/ws`;

  async function post(path: string, body: unknown) {
    const res = await fetch(base + path, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    return { status: res.status, json: await res.json() };
  }

  console.log("1. register a device anonymously (no account needed) -> deviceId + hostToken");
  const dev = await post("/api/devices", { name: "Owner's Desktop" });
  assert.equal(dev.status, 201);
  const { deviceId, hostToken } = dev.json as { deviceId: string; hostToken: string };
  assert.ok(deviceId && hostToken);

  console.log("2. host connects over WS and authenticates with its device token");
  const hostWs = new WebSocket(wsBase);
  const hostMsgs: any[] = [];
  await new Promise<void>((resolve) => hostWs.once("open", () => resolve()));
  hostWs.on("message", (raw) => hostMsgs.push(JSON.parse(raw.toString())));
  hostWs.send(JSON.stringify({ type: "host:auth", deviceId, hostToken }));
  await waitFor(() => hostMsgs.some((m) => m.type === "host:auth-ok"));
  assert.ok(hostMsgs.find((m) => m.type === "host:auth-ok"));

  console.log("3. host requests a pairing code");
  hostWs.send(JSON.stringify({ type: "host:pairing-generate" }));
  await waitFor(() => hostMsgs.some((m) => m.type === "host:pairing-code"));
  const code = hostMsgs.find((m) => m.type === "host:pairing-code").code as string;
  assert.equal(code.length, 8);

  console.log("4. controller connects and says hello with just a display name (no account)");
  const ctrlWs = new WebSocket(wsBase);
  const ctrlMsgs: any[] = [];
  await new Promise<void>((resolve) => ctrlWs.once("open", () => resolve()));
  ctrlWs.on("message", (raw) => ctrlMsgs.push(JSON.parse(raw.toString())));
  ctrlWs.send(JSON.stringify({ type: "controller:hello", displayName: "Test Controller" }));
  await waitFor(() => ctrlMsgs.some((m) => m.type === "controller:hello-ok"));

  console.log("4a. wrong code is rejected (not consumed as the real pairing)");
  ctrlWs.send(JSON.stringify({ type: "pairing:attempt", deviceId, code: "WRONGCODE" }));
  await waitFor(() => ctrlMsgs.some((m) => m.type === "pairing:rejected"));
  assert.equal(ctrlMsgs.find((m) => m.type === "pairing:rejected").reason, "not_found");

  console.log("5. controller attempts pairing with the real code");
  ctrlWs.send(JSON.stringify({ type: "pairing:attempt", deviceId, code }));
  await waitFor(() => ctrlMsgs.some((m) => m.type === "pairing:pending"));
  await waitFor(() => hostMsgs.some((m) => m.type === "pairing:incoming-request"));
  const incoming = hostMsgs.find((m) => m.type === "pairing:incoming-request");
  const sessionRequestId = incoming.sessionRequestId;
  assert.equal(incoming.controllerName, "Test Controller");

  console.log("6. re-using the same code a second time must fail (single-use)");
  const ctrlWs2 = new WebSocket(wsBase);
  const ctrlMsgs2: any[] = [];
  await new Promise<void>((resolve) => ctrlWs2.once("open", () => resolve()));
  ctrlWs2.on("message", (raw) => ctrlMsgs2.push(JSON.parse(raw.toString())));
  ctrlWs2.send(JSON.stringify({ type: "controller:hello", displayName: "Second Controller" }));
  await waitFor(() => ctrlMsgs2.some((m) => m.type === "controller:hello-ok"));
  ctrlWs2.send(JSON.stringify({ type: "pairing:attempt", deviceId, code }));
  await waitFor(() => ctrlMsgs2.some((m) => m.type === "pairing:rejected"));
  assert.equal(ctrlMsgs2.find((m) => m.type === "pairing:rejected").reason, "already_used");
  ctrlWs2.close();

  console.log("7. host approves the (still) pending request");
  hostWs.send(JSON.stringify({ type: "host:respond", sessionRequestId, accept: true }));
  await waitFor(() => ctrlMsgs.some((m) => m.type === "session:started"));
  await waitFor(() => hostMsgs.some((m) => m.type === "session:started"));
  const sessionId = ctrlMsgs.find((m) => m.type === "session:started").sessionId;
  assert.ok(sessionId);

  console.log("8. controller sends a fake SDP offer, host receives it over the relay");
  ctrlWs.send(JSON.stringify({ type: "signal", sessionId, data: { sdp: "fake-offer" } }));
  await waitFor(() => hostMsgs.some((m) => m.type === "signal" && m.data?.sdp === "fake-offer"));

  console.log("9. host ends the session, controller is notified");
  hostWs.send(JSON.stringify({ type: "session:end", sessionId }));
  await waitFor(() => ctrlMsgs.some((m) => m.type === "session:ended"));
  assert.equal(ctrlMsgs.find((m) => m.type === "session:ended").reason, "ended_by_host");

  hostWs.close();
  ctrlWs.close();
  server.close();
  db.close();
  for (const suffix of ["", "-wal", "-shm"]) {
    fs.rmSync(process.env.DB_PATH! + suffix, { force: true });
  }

  console.log("\nALL E2E CHECKS PASSED");
}

function waitFor(pred: () => boolean, timeoutMs = 3000): Promise<void> {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const iv = setInterval(() => {
      if (pred()) {
        clearInterval(iv);
        resolve();
      } else if (Date.now() - start > timeoutMs) {
        clearInterval(iv);
        reject(new Error("timed out waiting for condition"));
      }
    }, 20);
  });
}

main().catch((err) => {
  console.error("E2E TEST FAILED:", err);
  process.exit(1);
});
