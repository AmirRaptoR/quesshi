import { useState } from "preact/hooks";
import { useApp } from "../app";
import { initial, tint } from "../avatar";
import { clientFeatures } from "../features";
import { pickTab, PLAY_TAB_KEY } from "../play-tabs";
import { usePlayer } from "../player";
import { stored } from "../storage";

/** Today's play hub: the enabled features' tabs, the chosen one remembered. */
export function Play() {
  const { t } = useApp();
  const me = usePlayer();
  const tabs = clientFeatures.flatMap((feature) => feature.playTabs ?? []);
  const [tab, setTab] = useState(() => pickTab(stored.get(PLAY_TAB_KEY), tabs.map((candidate) => candidate.id)));
  if (!me) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  const current = tabs.find((candidate) => candidate.id === tab);
  const choose = (id: string) => {
    setTab(id);
    stored.set(PLAY_TAB_KEY, id);
  };

  return (
    <div class="shell">
      <header class="home-head">
        <div class="home-mark">{t.t("app.name")}</div>
        <div class="home-score">
          <span class="avatar home-score__face" style={`background:${tint(me.avatarSeed)}`}>{initial(me.displayName)}</span>
        </div>
      </header>

      {tabs.length > 1 && (
        <div class="home-tabs" role="tablist" aria-label={t.t("home.tabs")}>
          {tabs.map((candidate) => (
            <button type="button" role="tab" id={`home-tab-${candidate.id}`} aria-controls={`home-panel-${candidate.id}`} aria-selected={candidate.id === tab}
              class={`home-tabs__tab${candidate.id === tab ? " is-on" : ""}`} onClick={() => choose(candidate.id)}>
              {t.t(candidate.label)}
            </button>
          ))}
        </div>
      )}

      {current && (
        // Keyed so a switch of tab closes whatever the last one had half open, as today.
        <div class="enter" role="tabpanel" id={`home-panel-${current.id}`} aria-labelledby={`home-tab-${current.id}`} key={current.id}>
          <current.panel />
        </div>
      )}
    </div>
  );
}
