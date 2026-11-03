import { createHmac, timingSafeEqual } from "node:crypto";
import type { Lang } from "./identity.store";

const MAX_AGE_SECONDS = 24 * 3600;

export interface TelegramUser { id: string; name: string; lang: Lang | null }

/**
 * Checks the Mini App's initData as Telegram documents it and returns the user, or null when the
 * signature is wrong, the user is missing, or auth_date is older than a day.
 */
export function verifyInitData(initData: string, botToken: string, now: Date): TelegramUser | null {
  const params = new URLSearchParams(initData);
  const hash = params.get("hash");
  if (!hash || !/^[0-9a-f]{64}$/.test(hash)) return null;
  params.delete("hash");
  const check = [...params.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([key, value]) => `${key}=${value}`).join("\n");
  const secret = createHmac("sha256", "WebAppData").update(botToken).digest();
  const expected = createHmac("sha256", secret).update(check).digest();
  if (!timingSafeEqual(expected, Buffer.from(hash, "hex"))) return null;
  const authDate = Number(params.get("auth_date"));
  if (!Number.isInteger(authDate) || now.getTime() / 1000 - authDate > MAX_AGE_SECONDS) return null;
  let user: { id?: unknown; first_name?: unknown; last_name?: unknown; language_code?: unknown } | null;
  try {
    user = JSON.parse(params.get("user") ?? "");
  } catch {
    return null;
  }
  if (typeof user?.id !== "number" || !Number.isSafeInteger(user.id)) return null;
  const name = [user.first_name, user.last_name].filter((part): part is string => typeof part === "string").join(" ").trim();
  const code = user.language_code;
  return { id: String(user.id), name: name || "player", lang: code === "fa" || code === "en" || code === "nl" ? code : null };
}
