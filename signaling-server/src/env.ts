import "dotenv/config";
import path from "node:path";

export const env = {
  port: Number(process.env.PORT ?? 8443),
  dbPath: path.resolve(process.env.DB_PATH ?? "./data/remote-desktop.db"),
  allowedOrigins: (process.env.ALLOWED_ORIGINS ?? "http://localhost:5173")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean),
  pairingCodeTtlSeconds: Number(process.env.PAIRING_CODE_TTL_SECONDS ?? 180),
  pairingMaxAttempts: Number(process.env.PAIRING_MAX_ATTEMPTS ?? 5),
  pairingLockoutWindowSeconds: Number(
    process.env.PAIRING_LOCKOUT_WINDOW_SECONDS ?? 300
  ),
  iceServers: JSON.parse(
    process.env.ICE_SERVERS ?? '[{"urls":"stun:stun.l.google.com:19302"}]'
  ) as Array<{ urls: string; username?: string; credential?: string }>,
  isProd: process.env.NODE_ENV === "production",
};
