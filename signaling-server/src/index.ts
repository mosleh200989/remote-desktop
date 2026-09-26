import cors from "cors";
import express from "express";
import { createServer } from "node:http";
import { WebSocketServer } from "ws";
import "./db"; // ensure schema is created before anything else
import { env } from "./env";
import { authRouter } from "./routes/auth";
import { devicesRouter } from "./routes/devices";
import { attachSignalingHub } from "./ws/hub";
import { clearExpiredPairingArtifacts } from "./pairing";

const app = express();
app.use(express.json({ limit: "16kb" }));
app.use(
  cors({
    origin: env.allowedOrigins,
    credentials: false,
  })
);

app.get("/health", (_req, res) => res.json({ ok: true }));
app.use("/api/auth", authRouter);
app.use("/api/devices", devicesRouter);

const server = createServer(app);
const wss = new WebSocketServer({ server, path: "/ws" });
attachSignalingHub(wss);

setInterval(clearExpiredPairingArtifacts, 60_000).unref();

server.listen(env.port, () => {
  console.log(`Signaling server listening on :${env.port} (ws path /ws)`);
});
