import { customAlphabet } from "nanoid";
import crypto from "node:crypto";
import { db } from "./db";
import { env } from "./env";
import { hashHostToken } from "./auth";

// Public device IDs: 9 digits, grouped for display like "482 917 305".
// Not secret; only identifies which host to pair with.
const deviceIdAlphabet = customAlphabet("0123456789", 9);

// Pairing codes: 8 chars from an unambiguous alphabet (no 0/O/1/I/L), ~38 bits
// of entropy, short-lived and single-use. That's the actual secret.
const pairingCodeAlphabet = customAlphabet(
  "ABCDEFGHJKMNPQRSTUVWXYZ23456789",
  8
);

export interface DeviceRow {
  id: number;
  device_id: string;
  host_token_hash: string;
  name: string;
  created_at: number;
  last_seen_at: number | null;
}

export async function registerDevice(
  name: string
): Promise<{ device: DeviceRow; hostToken: string }> {
  const deviceId = deviceIdAlphabet();
  const hostToken = crypto.randomBytes(32).toString("base64url");
  const hostTokenHash = await hashHostToken(hostToken);

  db.prepare(
    `INSERT INTO devices (device_id, host_token_hash, name, created_at)
     VALUES (?, ?, ?, ?)`
  ).run(deviceId, hostTokenHash, name, Date.now());

  const device = getDeviceByDeviceId(deviceId)!;
  return { device, hostToken };
}

// No accounts means device registration is otherwise open, so rate-limit it
// per IP to blunt someone spamming rows into the devices table.
export function checkAndConsumeRegistrationRateLimit(ip: string): boolean {
  const windowStart = Date.now() - 3_600_000;
  const recent = db
    .prepare("SELECT COUNT(*) as n FROM device_registrations WHERE ip = ? AND registered_at > ?")
    .get(ip, windowStart) as { n: number };
  if (recent.n >= 10) return false;
  db.prepare("INSERT INTO device_registrations (ip, registered_at) VALUES (?, ?)").run(
    ip,
    Date.now()
  );
  return true;
}

export function getDeviceByDeviceId(deviceId: string): DeviceRow | undefined {
  return db.prepare("SELECT * FROM devices WHERE device_id = ?").get(deviceId) as
    | DeviceRow
    | undefined;
}

export function touchDeviceLastSeen(deviceId: string): void {
  db.prepare("UPDATE devices SET last_seen_at = ? WHERE device_id = ?").run(
    Date.now(),
    deviceId
  );
}

export function createPairingCode(deviceId: string): { code: string; expiresAt: number } {
  const code = pairingCodeAlphabet();
  const expiresAt = Date.now() + env.pairingCodeTtlSeconds * 1000;
  db.prepare(
    "INSERT INTO pairing_codes (code, device_id, expires_at, used, created_at) VALUES (?, ?, ?, 0, ?)"
  ).run(code, deviceId, expiresAt, Date.now());
  return { code, expiresAt };
}

export type PairingCheckResult =
  | { ok: true; deviceId: string }
  | { ok: false; reason: "locked_out" | "not_found" | "expired" | "already_used" };

// Rate limiting: record every attempt (success or failure) and reject once a
// device has seen too many attempts within the lockout window, regardless of
// whether the codes were right. This blunts brute-forcing of the 8-char code.
export function checkAndConsumePairingCode(
  deviceId: string,
  code: string
): PairingCheckResult {
  const windowStart = Date.now() - env.pairingLockoutWindowSeconds * 1000;
  const recentAttempts = db
    .prepare(
      "SELECT COUNT(*) as n FROM pairing_attempts WHERE device_id = ? AND attempted_at > ?"
    )
    .get(deviceId, windowStart) as { n: number };

  if (recentAttempts.n >= env.pairingMaxAttempts) {
    return { ok: false, reason: "locked_out" };
  }

  db.prepare(
    "INSERT INTO pairing_attempts (device_id, attempted_at) VALUES (?, ?)"
  ).run(deviceId, Date.now());

  const row = db
    .prepare(
      "SELECT * FROM pairing_codes WHERE device_id = ? AND code = ?"
    )
    .get(deviceId, code.toUpperCase()) as
    | { code: string; device_id: string; expires_at: number; used: number }
    | undefined;

  if (!row) return { ok: false, reason: "not_found" };
  if (row.used) return { ok: false, reason: "already_used" };
  if (row.expires_at < Date.now()) return { ok: false, reason: "expired" };

  db.prepare("UPDATE pairing_codes SET used = 1 WHERE code = ?").run(row.code);
  return { ok: true, deviceId: row.device_id };
}

export function clearExpiredPairingArtifacts(): void {
  const now = Date.now();
  db.prepare("DELETE FROM pairing_codes WHERE expires_at < ?").run(now - 3_600_000);
  db.prepare("DELETE FROM pairing_attempts WHERE attempted_at < ?").run(
    now - env.pairingLockoutWindowSeconds * 1000
  );
  db.prepare("DELETE FROM device_registrations WHERE registered_at < ?").run(now - 3_600_000);
}
