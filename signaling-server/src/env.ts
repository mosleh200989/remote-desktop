import "dotenv/config";
import path from "node:path";

function required(name: string, fallback?: string): string {
  const v = process.env[name] ?? fallback;
  if (v === undefined) throw new Error(`Missing required env var ${name}`);
  return v;
}

export const env = {
  port: Number(process.env.PORT ?? 8443),
  jwtSecret: required("JWT_SECRET", "dev-only-insecure-secret-change-me"),
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

if (env.isProd && env.jwtSecret === "dev-only-insecure-secret-change-me") {
  throw new Error("Refusing to start in production with the default JWT_SECRET. Set a real one in .env");
}
