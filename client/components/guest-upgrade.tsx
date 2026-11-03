import { useState } from "preact/hooks";
import { ApiError, post } from "../api";
import { useApp } from "../app";

/** Today's "keep this account": a guest adds an email, confirms the emailed code, and stays who they are. */
export function GuestUpgrade() {
  const { t, refresh } = useApp();
  const [stage, setStage] = useState<"email" | "code">("email");
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [taken, setTaken] = useState(false);
  const looksLikeEmail = email.includes("@") && email.length > 4 && !email.includes(" ");

  const send = async () => {
    setBusy(true);
    setError(null);
    setTaken(false);
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
    setTaken(false);
    try {
      await post("/api/me/upgrade", { email: email.trim(), code: code.trim() });
      // The guest flag clears with the refresh, and with it this card.
      await refresh();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      const addressTaken = failure.code === "address_taken";
      setTaken(addressTaken);
      setError(t.t(addressTaken ? "lobby.upgrade.taken" : "auth.badCode"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="card enter">
      <div style="font-size:var(--step-0);font-weight:700">{t.t("lobby.upgrade.title")}</div>
      <p class="faint" style="margin-top:.4rem">{t.t("lobby.upgrade.blurb")}</p>
      <div class="stack" style="margin-top:.8rem">
        {stage === "email" ? (
          <>
            <input class="input" type="email" inputMode="email" autoComplete="email" dir="ltr" placeholder="you@example.com" aria-label={t.t("auth.email")}
              value={email} onInput={(e) => setEmail(e.currentTarget.value)} />
            <button class="btn btn--block" disabled={busy || !looksLikeEmail} onClick={() => void send()}>{busy ? t.t("common.loading") : t.t("auth.send")}</button>
          </>
        ) : (
          <>
            <p class="faint" dir="ltr" style="margin:0">{email}</p>
            <input class="input code-input" inputMode="numeric" maxLength={6} dir="ltr" autoComplete="one-time-code" aria-label={t.t("auth.code")}
              value={code} onInput={(e) => setCode(e.currentTarget.value)} />
            <button class="btn btn--block" disabled={busy || code.length !== 6} onClick={() => void verify()}>{busy ? t.t("common.loading") : t.t("auth.verify")}</button>
            <button class="btn btn--ghost btn--block btn--sm" disabled={busy} onClick={() => setStage("email")}>{t.t("auth.back")}</button>
          </>
        )}
        {error && (
          <div class="notice notice--bad">
            {error}
            {taken && <> <a href="/signin">{t.t("auth.title")}</a></>}
          </div>
        )}
      </div>
    </section>
  );
}
