import bcrypt from "bcryptjs";

const BCRYPT_ROUNDS = 12;

// Host device tokens are long-lived opaque secrets (not JWTs) generated once
// at device registration and stored only as a hash, like an API key. There
// are no user accounts in this app (AnyDesk-style): the device ID + pairing
// code + host approval are the entire authorization boundary.
export async function hashHostToken(token: string): Promise<string> {
  return bcrypt.hash(token, BCRYPT_ROUNDS);
}

export async function verifyHostToken(token: string, hash: string): Promise<boolean> {
  return bcrypt.compare(token, hash);
}
