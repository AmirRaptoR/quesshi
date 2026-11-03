import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../app";
import { LangToggle } from "../components/lang-toggle";
import { Shamseh } from "../components/shamseh";
import { GUEST_ROUTE_KEY, stored } from "../storage";

// A duel caught mid-play: four of mine answered, two of theirs. The mark says what the game is.
const MINE = [true, true, false, true, null, null];
const THEIRS = [true, false, null, null, null, null];
const STEPS = ["step1", "step2", "step3"];
const FEATURES = ["async", "bilingual", "speed", "guest"];

/** The public front door; anyone signed in is sent on, a guest to their one game. */
export function Landing() {
  const { me, t } = useApp();
  const { route } = useLocation();
  const [joining, setJoining] = useState(false);
  const [code, setCode] = useState("");

  useEffect(() => {
    if (me?.guest) {
      const back = stored.get(GUEST_ROUTE_KEY);
      if (back) route(back, true);
    } else if (me) route("/play", true);
  }, [me]);

  const join = () => route(`/join/${code.trim().toUpperCase()}`);

  return (
    <div class="shell" style="--shell:46rem">
      <section class="hero enter">
        <div class="hero__mark"><Shamseh mine={MINE} theirs={THEIRS} ignite label={t.t("app.name")} /></div>
        <h1>{t.t("app.name")}</h1>
        <p class="hero__lede">{t.t("landing.lede")}</p>
        <div class="hero__cta">
          <a class="btn" href="/signin?next=/play">{t.t("landing.cta")}</a>
          <button class="btn btn--ghost" onClick={() => setJoining(!joining)}>{t.t("landing.haveCode")}</button>
        </div>
        <p class="faint" style="margin-top:.7rem;font-size:var(--step--1)">{t.t("landing.ctaHint")}</p>
        {joining && (
          <div class="row enter" style="max-width:20rem;margin:1rem auto 0">
            <input class="input grow" dir="ltr" maxLength={6} placeholder={t.t("home.joinPlaceholder")} aria-label={t.t("home.joinPlaceholder")}
              style="text-transform:uppercase" value={code} onInput={(e) => setCode(e.currentTarget.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && code.length >= 4) join(); }} />
            <button class="btn btn--gold" disabled={code.length < 4} onClick={join}>→</button>
          </div>
        )}
      </section>

      <section class="section enter">
        <h2>{t.t("landing.how")}</h2>
        <div class="card card--taq">
          <div class="steps">
            {STEPS.map((step) => (
              <div class="step">
                <div>
                  <h3>{t.t(`landing.${step}.title`)}</h3>
                  <p class="muted" style="margin:.2rem 0 0">{t.t(`landing.${step}.body`)}</p>
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section class="section enter">
        <h2>{t.t("landing.why")}</h2>
        <div class="grid-3" style="margin-top:.8rem">
          {FEATURES.map((feature) => (
            <div class="card card--flat">
              <h3 style="font-size:var(--step-0)">{t.t(`landing.${feature}.title`)}</h3>
              <p class="muted" style="margin:.3rem 0 0;font-size:var(--step--1)">{t.t(`landing.${feature}.body`)}</p>
            </div>
          ))}
        </div>
      </section>

      <footer class="landing__foot enter">
        <a class="btn btn--block" href="/signin?next=/play">{t.t("landing.cta")}</a>
        <div class="row" style="justify-content:center;margin-top:1rem">
          <LangToggle />
          <a class="btn btn--ghost btn--sm" href="/signin?next=/play">{t.t("auth.title")}</a>
        </div>
        <p class="faint" style="margin-top:1.2rem;font-size:var(--step--1)">{t.t("landing.foot")}</p>
      </footer>
    </div>
  );
}
