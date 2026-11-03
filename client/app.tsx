import { createContext, type ComponentChildren } from "preact";
import { useCallback, useContext, useEffect, useState } from "preact/hooks";
import { ApiError, get, post } from "./api";
import { roomOf, signInWithTelegram, telegramLaunch } from "./telegram";
import { LANGS, loadTranslator, type Lang, type Translator } from "./i18n";

/** The signed-in player, as /api/me returns them. */
export interface Me { id: string; displayName: string; email: string | null; avatarSeed: string; lang: Lang; guest: boolean }

interface AppState {
  ready: boolean;
  me: Me | null;
  t: Translator;
  /** Reloads who is signed in, after signing in or out. */
  refresh(): Promise<void>;
  setLang(lang: Lang): Promise<void>;
}

const LANG_KEY = "quesshi.lang";
const Context = createContext<AppState | null>(null);

export function useApp(): AppState {
  const state = useContext(Context);
  if (!state) throw new Error("useApp needs an AppProvider.");
  return state;
}

const storedLang = (): Lang => {
  try {
    const value = localStorage.getItem(LANG_KEY);
    return LANGS.includes(value as Lang) ? (value as Lang) : "fa";
  } catch {
    return "fa";
  }
};

function applyDirection(t: Translator) {
  document.documentElement.lang = t.lang;
  document.documentElement.dir = t.rtl ? "rtl" : "ltr";
}

/** Loads the language and who is signed in before the first page shows, as today. */
export function AppProvider({ children }: { children: ComponentChildren }) {
  const [t, setT] = useState<Translator | null>(null);
  const [me, setMe] = useState<Me | null>(null);
  const [checked, setChecked] = useState(false);

  /** Reads who is signed in; true when somebody is. */
  const load = useCallback(async () => {
    try {
      setMe(await get<Me>("/api/me"));
      return true;
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      setMe(null);
      return false;
    }
  }, []);

  const refresh = useCallback(async () => {
    await load();
    setChecked(true);
  }, []);

  const setLang = useCallback(async (lang: Lang) => {
    const next = await loadTranslator(lang);
    try {
      localStorage.setItem(LANG_KEY, lang);
    } catch {
      // Private windows may refuse storage; the language still applies to this visit.
    }
    applyDirection(next);
    setT(next);
  }, []);

  useEffect(() => {
    void setLang(storedLang());
    void start();
  }, []);

  /** Inside Telegram, nobody signed in is signed in with their Telegram account, and a room link opens joining that room. */
  const start = async () => {
    const signedIn = await load();
    const launch = telegramLaunch();
    if (launch) {
      const room = roomOf(launch.startParam);
      if (room && location.pathname === "/") history.replaceState(null, "", `/join/${room}`);
      if (!signedIn && await signInWithTelegram(launch, post)) await load();
    }
    setChecked(true);
  };

  if (!t || !checked) return <div class="spinner" role="status" aria-label="Loading" />;
  return <Context.Provider value={{ ready: true, me, t, refresh, setLang }}>{children}</Context.Provider>;
}
