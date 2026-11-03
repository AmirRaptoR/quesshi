import { pbkdf2Sync, timingSafeEqual } from "node:crypto";

/** New hashes use argon2id. */
export function hashPassword(password: string): Promise<string> {
  return Bun.password.hash(password, { algorithm: "argon2id" });
}

/** Verifies an argon2id hash, or a hash made by today's application (ASP.NET Identity, format v3). */
export async function verifyPassword(hash: string, password: string): Promise<boolean> {
  if (hash.startsWith("$argon2")) return Bun.password.verify(password, hash);
  return verifyIdentityV3(hash, password);
}

export function hashPasswordSync(password: string): string {
  return Bun.password.hashSync(password, { algorithm: "argon2id" });
}

let dummy: Promise<string> | null = null;
/** Spends about as long as a real check, so an unknown name answers no faster than a known one. */
export async function burnTime(): Promise<void> {
  dummy ??= hashPassword(crypto.randomUUID());
  await Bun.password.verify("not the password", await dummy);
}

const PRF = { 0: "sha1", 1: "sha256", 2: "sha512" } as const;

/** ASP.NET Identity v3: 0x01, PRF (uint32), iterations (uint32), salt length (uint32), salt, subkey; big-endian. */
function verifyIdentityV3(encoded: string, password: string): boolean {
  const bytes = Buffer.from(encoded, "base64");
  if (bytes.length < 13 || bytes[0] !== 0x01) return false;
  const digest = PRF[bytes.readUInt32BE(1) as keyof typeof PRF];
  const iterations = bytes.readUInt32BE(5);
  const saltLength = bytes.readUInt32BE(9);
  if (!digest || saltLength < 16 || 13 + saltLength >= bytes.length) return false;
  const salt = bytes.subarray(13, 13 + saltLength);
  const expected = bytes.subarray(13 + saltLength);
  const actual = pbkdf2Sync(password, salt, iterations, expected.length, digest);
  return timingSafeEqual(actual, expected);
}
