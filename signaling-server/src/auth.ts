import bcrypt from "bcryptjs";
import jwt from "jsonwebtoken";
import { db } from "./db";
import { env } from "./env";

export interface UserRow {
  id: number;
  email: string;
  password_hash: string;
  created_at: number;
}

const BCRYPT_ROUNDS = 12;

export async function createUser(email: string, password: string): Promise<UserRow> {
  const passwordHash = await bcrypt.hash(password, BCRYPT_ROUNDS);
  const stmt = db.prepare(
    "INSERT INTO users (email, password_hash, created_at) VALUES (?, ?, ?)"
  );
  const info = stmt.run(email.toLowerCase(), passwordHash, Date.now());
  return getUserById(Number(info.lastInsertRowid))!;
}

export function getUserByEmail(email: string): UserRow | undefined {
  return db
    .prepare("SELECT * FROM users WHERE email = ?")
    .get(email.toLowerCase()) as UserRow | undefined;
}

export function getUserById(id: number): UserRow | undefined {
  return db.prepare("SELECT * FROM users WHERE id = ?").get(id) as
    | UserRow
    | undefined;
}

export async function verifyPassword(user: UserRow, password: string): Promise<boolean> {
  return bcrypt.compare(password, user.password_hash);
}

export interface SessionTokenPayload {
  sub: number; // user id
  email: string;
}

export function signUserToken(user: UserRow): string {
  const payload: SessionTokenPayload = { sub: user.id, email: user.email };
  return jwt.sign(payload, env.jwtSecret, { expiresIn: "12h" });
}

export function verifyUserToken(token: string): SessionTokenPayload | null {
  try {
    return jwt.verify(token, env.jwtSecret) as unknown as SessionTokenPayload;
  } catch {
    return null;
  }
}

// Host device tokens are long-lived opaque secrets (not JWTs) generated once
// at device registration and stored only as a hash, like an API key.
export async function hashHostToken(token: string): Promise<string> {
  return bcrypt.hash(token, BCRYPT_ROUNDS);
}

export async function verifyHostToken(token: string, hash: string): Promise<boolean> {
  return bcrypt.compare(token, hash);
}
