import { DatabaseSync } from "node:sqlite";
import fs from "node:fs";
import path from "node:path";
import { env } from "./env";

fs.mkdirSync(path.dirname(env.dbPath), { recursive: true });

// node:sqlite (stable in Node 24+, experimental behind the scenes) gives us a
// synchronous, zero-native-dependency SQLite driver - no node-gyp / Visual
// Studio / build-essential needed on either Windows dev machines or the VPS.
export const db = new DatabaseSync(env.dbPath);
db.exec("PRAGMA journal_mode = WAL");
db.exec("PRAGMA foreign_keys = ON");

db.exec(`
CREATE TABLE IF NOT EXISTS users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  email TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS devices (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id TEXT NOT NULL UNIQUE,       -- public short ID shown on the host, e.g. "482 917 305"
  owner_user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  host_token_hash TEXT NOT NULL,        -- hash of the long-lived secret the host app authenticates with
  name TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  last_seen_at INTEGER
);

CREATE TABLE IF NOT EXISTS pairing_codes (
  code TEXT PRIMARY KEY,
  device_id TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
  expires_at INTEGER NOT NULL,
  used INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS pairing_attempts (
  device_id TEXT NOT NULL,
  attempted_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
  controller_user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  status TEXT NOT NULL, -- pending | active | ended | rejected
  created_at INTEGER NOT NULL,
  ended_at INTEGER
);

CREATE INDEX IF NOT EXISTS idx_pairing_attempts_device_time ON pairing_attempts(device_id, attempted_at);
`);
