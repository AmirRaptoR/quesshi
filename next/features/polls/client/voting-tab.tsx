import { useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../client/app";
import { CodeField } from "../../../client/components/code-field";

/** Today's voting choice on the play hub: create a lobby, join one by code, and the settings line. */
export function VotingTab() {
  const { t } = useApp();
  const { route } = useLocation();
  const [codeOpen, setCodeOpen] = useState(false);
  return (
    <>
      {/* Voting has its own setup and never uses the trivia settings; the lobby is created when it is done. */}
      <button type="button" class="home-primary" onClick={() => route("/play/voting")}>
        <svg class="home-primary__icon" width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <rect x="3" y="5" width="18" height="14" rx="3" />
          <path d="M3 10h18M8 5v5M16 5v5" />
        </svg>
        <span class="home-primary__text">
          <span class="home-primary__title">{t.t("home.createLobby")}</span>
          <span class="home-primary__hint">{t.t("home.createLobbyHint")}</span>
        </span>
        <svg class="home-primary__go home-go" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M9 6l6 6-6 6" />
        </svg>
      </button>

      <div class="home-pair">
        <button type="button" class={`home-tile${codeOpen ? " is-on" : ""}`} aria-expanded={codeOpen} onClick={() => setCodeOpen(!codeOpen)}>
          <svg class="home-tile__icon" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M4 7h16M4 12h10M4 17h7" />
          </svg>
          <span>{t.t("home.join")}</span>
        </button>
      </div>

      {codeOpen && <CodeField />}

      <button type="button" class="home-settings" onClick={() => route("/play/voting")}>
        <span class="home-settings__text">{t.t("voting.settings.summary")}</span>
        <span class="home-settings__change">{t.t("home.settingsChange")}</span>
      </button>
    </>
  );
}
