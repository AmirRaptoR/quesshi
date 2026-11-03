import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { ApiError, get, post } from "../../../../client/api";
import { initial, tint } from "../logic/avatar";
import { lastSettings, remember, settingsLine } from "../logic/duel-settings";
import { duelRoute, opponentOf, split, standing } from "../logic/home-duels";
import type { DuelRoomView } from "../types";

/** What "Create a lobby" opens with; seats are chosen in the lobby, where the roster is visible. */
const DEFAULT_CAPACITY = 2;

const Go = ({ class: className }: { class: string }) => (
  <svg class={className} width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
    <path d="M9 6l6 6-6 6" />
  </svg>
);

/** The settings that used to be a whole form, as one line, and the way to change them. */
function SettingsLine() {
  const { t } = useApp();
  const { route } = useLocation();
  return (
    <button type="button" class="home-settings" onClick={() => route("/play/questions")}>
      <span class="home-settings__text">{settingsLine(lastSettings(t.lang), t)}</span>
      <span class="home-settings__change">{t.t("home.settingsChange")}</span>
    </button>
  );
}

/** The code field: a code is a code, so the join page decides which game it opens. */
function CodeField() {
  const { t } = useApp();
  const { route } = useLocation();
  const [code, setCode] = useState("");
  const join = () => route(`/join/${code.trim().toUpperCase()}`);
  return (
    <div class="home-code">
      <input class="input grow" dir="ltr" maxLength={6} style="text-transform:uppercase" placeholder={t.t("home.joinPlaceholder")} aria-label={t.t("home.joinPlaceholder")}
        value={code} onInput={(e) => setCode(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === "Enter" && code.length >= 4) join(); }} />
      <button type="button" class="btn btn--gold home-code__go" aria-label={t.t("home.join")} disabled={code.length < 4} onClick={join}>
        <Go class="home-go" />
      </button>
    </div>
  );
}

/** Opens a lobby of the format at the remembered settings and goes to it. */
function useCreate(segment: "live" | "duel") {
  const { t } = useApp();
  const { route } = useLocation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const create = async () => {
    setBusy(true);
    setError(null);
    const settings = lastSettings(t.lang);
    remember(settings);
    try {
      const room = await post<{ id: string; code: string }>(`/f/trivia/${segment}/lobby`, { ...settings, capacity: DEFAULT_CAPACITY });
      route(`/lobby/${room.code}`);
    } catch (failure) {
      setError(t.t(failure instanceof ApiError && failure.code === "live_disabled" ? "live.disabled" : "common.error"));
      setBusy(false);
    }
  };
  return { busy, error, create };
}

/** Today's Live tab: open a lobby to share, or join one by its code. */
export function LiveTab() {
  const { t } = useApp();
  const { busy, error, create } = useCreate("live");
  const [codeOpen, setCodeOpen] = useState(false);
  const [liveEnabled, setLiveEnabled] = useState(true);
  useEffect(() => {
    get<{ liveEnabled: boolean }>("/f/trivia/limits").then((limits) => setLiveEnabled(limits.liveEnabled), () => {});
  }, []);
  return (
    <div class="enter">
      {!liveEnabled && <div class="notice" style="margin-top:1rem">{t.t("live.disabled")}</div>}
      <button type="button" class="home-primary" disabled={busy || !liveEnabled} onClick={() => void create()}>
        {/* A room with seats along its sides: more than two people, all here at once. */}
        <svg class="home-primary__icon" width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <rect x="3" y="5" width="18" height="14" rx="3" />
          <path d="M3 10h18M8 5v5M16 5v5" />
        </svg>
        <span class="home-primary__text">
          <span class="home-primary__title">{t.t("home.createLobby")}</span>
          <span class="home-primary__hint">{t.t("home.createLobbyHint")}</span>
        </span>
        <Go class="home-primary__go home-go" />
      </button>
      <div class="home-pair">
        <button type="button" class={`home-tile${codeOpen ? " is-on" : ""}`} aria-expanded={codeOpen} disabled={busy} onClick={() => setCodeOpen(!codeOpen)}>
          <svg class="home-tile__icon" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M4 7h16M4 12h10M4 17h7" />
          </svg>
          <span>{t.t("home.join")}</span>
        </button>
      </div>
      {codeOpen && <CodeField />}
      <SettingsLine />
      {error && <div class="notice notice--bad" style="margin-top:.9rem">{error}</div>}
    </div>
  );
}

/** Today's Offline tab: a new asynchronous duel, and the player's duels split by whose move it is. */
export function OfflineTab() {
  const { me, t } = useApp();
  const { route } = useLocation();
  const { busy, error, create } = useCreate("duel");
  const [opponent, setOpponent] = useState(false);
  const [codeOpen, setCodeOpen] = useState(false);
  const [duels, setDuels] = useState<DuelRoomView[]>([]);
  const meId = me?.id ?? "";
  useEffect(() => {
    get<DuelRoomView[]>("/f/trivia/duels").then(setDuels, () => {});
  }, []);
  const lists = split(duels, meId);

  // The whole row is the target; the saffron pill marks the half the player can act on.
  const row = (duel: DuelRoomView, mine: boolean) => {
    const other = opponentOf(duel, meId);
    const [key, ...args] = standing(duel, meId);
    return (
      <button type="button" class={`home-duel${mine ? " home-duel--turn" : ""}`} key={duel.id} onClick={() => route(duelRoute(duel, meId))}>
        <span class="avatar home-duel__face" style={`background:${tint(other?.avatarSeed ?? duel.code)}`}>{initial(other?.displayName ?? "?")}</span>
        <span class="home-duel__who">
          {/* A duel nobody has joined is named by its code, the only handle anyone has on it. */}
          {other ? <span class="home-duel__name">{other.displayName}</span> : <span class="home-duel__name" dir="ltr">{duel.code}</span>}
          <span class="home-duel__sub">{t.format(key, ...args)}</span>
        </span>
        {mine && <span class="home-duel__play">{t.t("home.play")}</span>}
      </button>
    );
  };

  return (
    <div class="enter">
      <button type="button" class="home-primary" aria-expanded={opponent} disabled={busy} onClick={() => { setOpponent(!opponent); if (opponent) setCodeOpen(false); }}>
        {/* A pencil: a turn-based duel is something you put down and come back to. */}
        <svg class="home-primary__icon" width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M14.5 4.5l5 5-9.5 9.5H5v-5z" />
          <path d="M12.5 6.5l5 5" />
        </svg>
        <span class="home-primary__text">
          <span class="home-primary__title">{t.t("home.newDuel")}</span>
          <span class="home-primary__hint">{t.t("home.newDuelHint")}</span>
        </span>
        <Go class="home-primary__go home-go" />
      </button>
      {opponent && (
        <>
          <div class="home-choice" role="group" aria-label={t.t("home.opponentPick")}>
            <button type="button" class="home-choice__option" disabled={busy} onClick={() => void create()}>{t.t("home.opponentFriend")}</button>
            <button type="button" class={`home-choice__option${codeOpen ? " is-on" : ""}`} aria-expanded={codeOpen} disabled={busy}
              onClick={() => setCodeOpen(!codeOpen)}>{t.t("home.opponentByCode")}</button>
          </div>
          {codeOpen && <CodeField />}
        </>
      )}
      <SettingsLine />
      {lists.yourTurn.length > 0 && (
        <section class="home-sect">
          <h2 class="home-sect__title">{t.t("home.yourTurn")}</h2>
          <div class="home-duels">{lists.yourTurn.map((duel) => row(duel, true))}</div>
        </section>
      )}
      {lists.waitingOnThem.length > 0 && (
        <section class="home-sect">
          <h2 class="home-sect__title home-sect__title--quiet">{t.t("home.waitingOnThem")}</h2>
          <div class="home-duels">{lists.waitingOnThem.map((duel) => row(duel, false))}</div>
        </section>
      )}
      {lists.yourTurn.length === 0 && lists.waitingOnThem.length === 0 && <p class="empty">{t.t("home.noMatches")}</p>}
      {error && <div class="notice notice--bad" style="margin-top:.9rem">{error}</div>}
    </div>
  );
}
