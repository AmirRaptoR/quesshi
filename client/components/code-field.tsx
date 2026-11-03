import { useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../app";

/** Today's inline join field on the play hub; a code of any game leads to the join page, which knows its kind. */
export function CodeField() {
  const { t } = useApp();
  const { route } = useLocation();
  const [code, setCode] = useState("");
  const go = () => route(`/join/${code.trim().toUpperCase()}`);
  return (
    <div class="home-code">
      <input class="input grow" dir="ltr" maxLength={6} style="text-transform:uppercase" placeholder={t.t("home.joinPlaceholder")} aria-label={t.t("home.joinPlaceholder")}
        value={code} onInput={(e) => setCode(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === "Enter" && code.trim().length >= 4) go(); }} />
      <button type="button" class="btn btn--gold home-code__go" aria-label={t.t("home.join")} disabled={code.trim().length < 4} onClick={go}>
        <svg class="home-go" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M9 6l6 6-6 6" />
        </svg>
      </button>
    </div>
  );
}
