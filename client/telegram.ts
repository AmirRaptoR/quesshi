/** What Telegram hands a Mini App when it opens: the signed initData and the link's start parameter. */
export interface TelegramLaunch { initData: string; startParam: string | null }

interface Env {
  Telegram?: { WebApp?: { initData?: string; initDataUnsafe?: { start_param?: string } } };
  location: { hash: string };
}

/** The launch data when the page runs inside Telegram, else null. Telegram offers it on its WebApp object and in the URL hash. */
export function telegramLaunch(env: Env = window as unknown as Env): TelegramLaunch | null {
  const app = env.Telegram?.WebApp;
  const fromHash = new URLSearchParams(env.location.hash.replace(/^#/, ""));
  const initData = app?.initData || fromHash.get("tgWebAppData") || "";
  if (!initData) return null;
  const startParam = app?.initDataUnsafe?.start_param || fromHash.get("tgWebAppStartParam") || new URLSearchParams(initData).get("start_param") || null;
  return { initData, startParam };
}

/** The room a `room_<code>` start parameter names, or null for any other. */
export function roomOf(startParam: string | null): string | null {
  const code = /^room_([A-Za-z0-9]{1,12})$/.exec(startParam ?? "")?.[1];
  return code ? code.toUpperCase() : null;
}

/** Signs in with the launch data; false when Telegram sign-in was refused or failed. */
export async function signInWithTelegram(launch: TelegramLaunch, post: (path: string, body: unknown) => Promise<unknown>): Promise<boolean> {
  try {
    await post("/api/auth/telegram", { initData: launch.initData });
    return true;
  } catch {
    return false;
  }
}
