import { useEffect, useRef, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, get, post } from "../api";
import { useApp } from "../app";
import { LangToggle } from "../components/lang-toggle";
import { Shamseh } from "../components/shamseh";

// Decorative: a half-lit rosette, so the mark says "a duel in progress" rather than "a logo".
const MARK = [true, true, false, true, null, null];

interface GoogleIdentity {
  accounts: { id: { initialize(options: { client_id: string; callback: (response: { credential: string }) => void }): void; renderButton(element: HTMLElement, options: Record<string, unknown>): void } };
}

/** Only a path on this site: anything that could point at another host is an open redirect. */
export function destination(next: string | undefined): string {
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/play";
}

function loadGoogle(): Promise<GoogleIdentity> {
  const existing = (window as { google?: GoogleIdentity }).google;
  if (existing) return Promise.resolve(existing);
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = "https://accounts.google.com/gsi/client";
    script.async = true;
    script.onload = () => resolve((window as unknown as { google: GoogleIdentity }).google);
    script.onerror = reject;
    document.head.appendChild(script);
  });
}

/** Today's sign-in: an emailed six-digit code, or Google when it is configured. */
export function SignIn() {
  const { me, t, refresh } = useApp();
  const { route, query } = useLocation();
  const [stage, setStage] = useState<"email" | "code">("email");
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [google, setGoogle] = useState<string | null>(null);
  const button = useRef<HTMLDivElement>(null);
  const next = destination(query.next);
  const looksLikeEmail = email.includes("@") && email.length > 4 && !email.includes(" ");

  useEffect(() => {
    if (me && !me.guest) {
      route(next, true);
      return;
    }
    get<{ googleEnabled: boolean; googleClientId: string | null }>("/api/auth/config").then((config) => setGoogle(config.googleEnabled ? config.googleClientId : null)).catch(() => {});
  }, [me]);

  useEffect(() => {
    if (!google || !button.current) return;
    // Google is optional; the emailed code still works without it.
    loadGoogle().then((g) => {
      g.accounts.id.initialize({
        client_id: google,
        callback: async ({ credential }) => {
          try {
            await post("/api/auth/google", { idToken: credential, lang: t.lang });
            await refresh();
          } catch {
            setError(t.t("common.error"));
          }
        },
      });
      if (button.current) g.accounts.id.renderButton(button.current, { theme: "filled_black", size: "large", width: 320, shape: "pill" });
    }).catch(() => {});
  }, [google]);

  const sendCode = async () => {
    setBusy(true);
    setError(null);
    try {
      await post("/api/auth/code", { email: email.trim(), lang: t.lang });
      setCode("");
      setStage("code");
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("auth.badEmail"));
    } finally {
      setBusy(false);
    }
  };

  const verify = async () => {
    setBusy(true);
    setError(null);
    try {
      await post("/api/auth/code/verify", { email: email.trim(), code: code.trim(), lang: t.lang });
      await refresh();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("auth.badCode"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="shell">
      <div class="stack" style="margin-top:12vh">
        <div class="enter" style="text-align:center">
          <div style="width:9rem;margin-inline:auto"><Shamseh mine={MARK} theirs={MARK} ignite label={t.t("app.name")} /></div>
          <h1 style="margin-top:1.25rem">{t.t("app.name")}</h1>
          <p class="muted">{t.t("app.tagline")}</p>
        </div>

        <div class="card card--taq enter">
          {stage === "email" ? (
            <div class="stack">
              <div>
                <h2>{t.t("auth.title")}</h2>
                <p class="muted" style="margin-top:.4rem">{t.t("auth.blurb")}</p>
              </div>
              <div class="field">
                <label for="email">{t.t("auth.email")}</label>
                <input id="email" class="input" type="email" inputMode="email" autoComplete="email" dir="ltr" placeholder="you@example.com"
                  value={email} onInput={(e) => setEmail(e.currentTarget.value)}
                  onKeyDown={(e) => { if (e.key === "Enter" && looksLikeEmail) void sendCode(); }} />
              </div>
              {error && <div class="notice notice--bad">{error}</div>}
              <button class="btn btn--block" disabled={busy || !looksLikeEmail} onClick={() => void sendCode()}>{busy ? t.t("common.loading") : t.t("auth.send")}</button>
            </div>
          ) : (
            <div class="stack">
              <div>
                <h2>{t.t("auth.code")}</h2>
                <p class="muted" style="margin-top:.4rem" dir="ltr">{email}</p>
              </div>
              <input class="input code-input" inputMode="numeric" maxLength={6} dir="ltr" autoComplete="one-time-code"
                value={code} onInput={(e) => setCode(e.currentTarget.value)}
                onKeyDown={(e) => { if (e.key === "Enter" && code.length === 6) void verify(); }} />
              {error && <div class="notice notice--bad">{error}</div>}
              <button class="btn btn--block" disabled={busy || code.length !== 6} onClick={() => void verify()}>{busy ? t.t("common.loading") : t.t("auth.verify")}</button>
              <button class="btn btn--ghost btn--block btn--sm" onClick={() => setStage("email")}>{t.t("auth.back")}</button>
            </div>
          )}
        </div>

        {google && (
          <div class="enter" style="display:grid;justify-items:center;gap:.75rem">
            <span class="faint">— {t.t("auth.google")} —</span>
            <div ref={button} />
          </div>
        )}

        <div class="enter" style="text-align:center"><LangToggle /></div>
      </div>
    </div>
  );
}
