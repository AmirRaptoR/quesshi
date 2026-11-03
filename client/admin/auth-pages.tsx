import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, post } from "../api";
import { useApp } from "../app";
import { LangToggle } from "../components/lang-toggle";
import { Shamseh } from "../components/shamseh";
import { AdminGate, PasswordFields } from "./components";
import { adminNext, clock, isPlaceholderEmail, problemsText } from "./logic";
import { adminSession, useAdminSession, type AdminMe } from "./session";

const MARK = [true, false, true, null, null, null];

export function AdminLogin() {
  const { t } = useApp();
  const { query, route } = useLocation();
  const session = useAdminSession();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<{ reason: string; lockedUntil: string | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const ready = username.trim().length > 0 && password.length > 0;

  useEffect(() => {
    if (session.status === "signed_in") route(session.admin.mustChangePassword ? "/admin/password" : adminNext(query.next), true);
  }, [session.status]);

  const signIn = async () => {
    setBusy(true);
    setError(null);
    try {
      const admin = await post<AdminMe>("/api/admin/auth/login", { username: username.trim(), password });
      adminSession.signedIn(admin);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      const body = failure.body as { lockedUntil?: unknown } | null;
      setError({ reason: failure.code === "admin.auth.locked" ? failure.code : "admin.auth.invalid", lockedUntil: typeof body?.lockedUntil === "string" ? body.lockedUntil : null });
      setPassword("");
    } finally {
      setBusy(false);
    }
  };
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Enter" && ready) void signIn();
  };

  return (
    <div class="shell">
      <div class="stack" style="margin-top:12vh">
        <div class="enter" style="text-align:center">
          <div style="width:6rem;margin-inline:auto"><Shamseh mine={MARK} theirs={MARK} label={t.t("app.name")} /></div>
          <h1 style="margin-top:1rem;font-size:var(--step-2)">{t.t("admin.auth.title")}</h1>
          <p class="muted">{t.t("admin.auth.blurb")}</p>
        </div>

        <div class="card card--taq enter">
          <div class="stack">
            <div class="field">
              <label for="username">{t.t("admin.auth.username")}</label>
              <input id="username" class="input" dir="ltr" autoComplete="username" value={username} onInput={(e) => setUsername(e.currentTarget.value)} onKeyDown={onKey} />
            </div>
            <div class="field">
              <label for="password">{t.t("admin.auth.password")}</label>
              <input id="password" class="input" type="password" dir="ltr" autoComplete="current-password" value={password} onInput={(e) => setPassword(e.currentTarget.value)} onKeyDown={onKey} />
            </div>
            {error && (
              <div class="notice notice--bad">
                {t.t(error.reason)}
                {error.lockedUntil && <div class="faint" style="margin-top:.3rem">{t.format("admin.auth.lockedUntil", clock(error.lockedUntil))}</div>}
              </div>
            )}
            <button class="btn btn--block" disabled={busy || !ready} onClick={() => void signIn()}>{busy ? t.t("common.loading") : t.t("admin.auth.signIn")}</button>
            <a class="btn btn--ghost btn--block btn--sm" href="/admin/forgot">{t.t("admin.auth.forgot")}</a>
          </div>
        </div>

        <div class="enter" style="display:grid;justify-items:center;gap:.6rem">
          <LangToggle />
          <a class="faint" style="font-size:var(--step--1)" href="/">{t.t("admin.backToGame")}</a>
        </div>
      </div>
    </div>
  );
}

export function AdminForgot() {
  const { t } = useApp();
  const [handle, setHandle] = useState("");
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const send = async () => {
    setBusy(true);
    // The answer is the same whether or not the account exists, so a failure reads the same too.
    await post("/api/admin/auth/forgot", { handle: handle.trim() }).catch(() => {});
    setSent(true);
    setBusy(false);
  };
  return (
    <div class="shell">
      <div class="stack" style="margin-top:14vh">
        <div class="card card--taq enter">
          <div class="stack">
            <div>
              <span class="eyebrow">{t.t("admin.auth.forgotTitle")}</span>
              <p class="muted" style="margin-top:.4rem">{t.t("admin.auth.forgotBlurb")}</p>
            </div>
            {!sent ? (
              <>
                <div class="field">
                  <label for="handle">{t.t("admin.auth.usernameOrEmail")}</label>
                  <input id="handle" class="input" dir="ltr" value={handle} onInput={(e) => setHandle(e.currentTarget.value)}
                    onKeyDown={(e) => { if (e.key === "Enter" && handle.trim()) void send(); }} />
                </div>
                <button class="btn btn--block" disabled={busy || !handle.trim()} onClick={() => void send()}>{busy ? t.t("common.loading") : t.t("admin.auth.sendLink")}</button>
              </>
            ) : <div class="notice notice--good">{t.t("admin.auth.sentIfExists")}</div>}
            <a class="btn btn--ghost btn--block btn--sm" href="/admin/login">{t.t("admin.auth.backToLogin")}</a>
          </div>
        </div>
      </div>
    </div>
  );
}

export function AdminReset() {
  const { t } = useApp();
  const { query } = useLocation();
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const submit = async () => {
    setMessage(null);
    if (password !== repeat) return setMessage(t.t("admin.auth.mismatch"));
    setBusy(true);
    try {
      await post("/api/admin/auth/reset", { token: query.token ?? "", password });
      setDone(true);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setMessage(problemsText(t, failure));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div class="shell">
      <div class="stack" style="margin-top:14vh">
        <div class="card card--taq enter">
          <div class="stack">
            <span class="eyebrow">{t.t("admin.auth.resetTitle")}</span>
            {done ? (
              <>
                <div class="notice notice--good">{t.t("admin.auth.resetDone")}</div>
                <a class="btn btn--block" href="/admin/login">{t.t("admin.auth.signIn")}</a>
              </>
            ) : (
              <>
                <PasswordFields password={password} repeat={repeat} onPassword={setPassword} onRepeat={setRepeat} />
                {message && <div class="notice notice--bad">{message}</div>}
                <button class="btn btn--block" disabled={busy || !password || !repeat} onClick={() => void submit()}>{busy ? t.t("common.loading") : t.t("admin.auth.setPassword")}</button>
                <a class="btn btn--ghost btn--block btn--sm" href="/admin/login">{t.t("admin.auth.backToLogin")}</a>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

/** Today's change-password page: the one page an admin with a temporary password may reach. */
export function AdminPassword() {
  return (
    <AdminGate allowMustChange>
      {(admin) => <div class="shell"><PasswordForm admin={admin} /></div>}
    </AdminGate>
  );
}

function PasswordForm({ admin }: { admin: AdminMe }) {
  const { t } = useApp();
  const { route } = useLocation();
  const [email, setEmail] = useState(admin.email);
  const [current, setCurrent] = useState("");
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [message, setMessage] = useState<{ text: string; ok: boolean } | null>(null);
  const [emailMessage, setEmailMessage] = useState<{ text: string; ok: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [savingEmail, setSavingEmail] = useState(false);

  const saveEmail = async () => {
    setSavingEmail(true);
    setEmailMessage(null);
    try {
      await post("/api/admin/auth/email", { email: email.trim() });
      setEmailMessage({ text: t.t("admin.auth.emailSaved"), ok: true });
      await adminSession.reload();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setEmailMessage({ text: t.t(failure.code), ok: false });
    } finally {
      setSavingEmail(false);
    }
  };

  const submit = async () => {
    setMessage(null);
    const wasForced = admin.mustChangePassword;
    if (password !== repeat) return setMessage({ text: t.t("admin.auth.mismatch"), ok: false });
    setBusy(true);
    try {
      await post("/api/admin/auth/password", { current, next: password });
      setMessage({ text: t.t("admin.auth.changed"), ok: true });
      setCurrent("");
      setPassword("");
      setRepeat("");
      await adminSession.reload();
      // A forced change is a gate: once done, get out of the way. A voluntary one stays, so the confirmation shows.
      if (wasForced) route("/admin");
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setMessage({ text: problemsText(t, failure), ok: false });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="stack" style="margin-top:8vh">
      <div class="card card--taq enter">
        <div class="stack">
          <div>
            <span class="eyebrow">{t.t("admin.auth.changeTitle")}</span>
            <p class="faint" style="margin-top:.4rem;font-size:var(--step--1)">{t.format("admin.auth.signedInAs", admin.username)}</p>
          </div>
          {admin.mustChangePassword && <div class="notice">{t.t("admin.auth.mustChange")}</div>}

          <div class="field">
            <label for="email">{t.t("admin.auth.email")}</label>
            <div class="row">
              <input id="email" class="input grow" type="email" dir="ltr" value={email} onInput={(e) => setEmail(e.currentTarget.value)} />
              <button class="btn btn--ghost btn--sm" disabled={savingEmail || !email.trim()} onClick={() => void saveEmail()}>{t.t("admin.auth.saveEmail")}</button>
            </div>
            <span class="faint" style="font-size:var(--step--1)">{t.t("admin.auth.emailHint")}</span>
            {isPlaceholderEmail(admin.email) && <div class="notice notice--bad" style="margin-top:.4rem">{t.t("admin.auth.placeholder")}</div>}
            {emailMessage && <div class={`notice ${emailMessage.ok ? "notice--good" : "notice--bad"}`} style="margin-top:.4rem">{emailMessage.text}</div>}
          </div>

          <hr style="border:0;border-top:1px solid var(--line);margin:.4rem 0" />

          <div class="field">
            <label for="current">{t.t("admin.auth.currentPassword")}</label>
            <input id="current" class="input" type="password" dir="ltr" autoComplete="current-password" value={current} onInput={(e) => setCurrent(e.currentTarget.value)} />
          </div>
          <PasswordFields password={password} repeat={repeat} onPassword={setPassword} onRepeat={setRepeat} />
          {message && <div class={`notice ${message.ok ? "notice--good" : "notice--bad"}`}>{message.text}</div>}
          <button class="btn btn--block" disabled={busy || !current || !password || !repeat} onClick={() => void submit()}>{busy ? t.t("common.loading") : t.t("admin.auth.change")}</button>
          {!admin.mustChangePassword && <a class="btn btn--ghost btn--block btn--sm" href="/admin">{t.t("admin.dashboard")}</a>}
        </div>
      </div>
    </div>
  );
}
