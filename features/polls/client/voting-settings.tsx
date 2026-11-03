import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, get, post, put } from "../../../client/api";
import { useApp } from "../../../client/app";
import { Icon } from "../../../client/components/icon";
import { LANGS, type Lang } from "../../../client/i18n";
import { destination } from "../../../client/pages/signin";
import { usePlayer } from "../../../client/player";
import { POLL_API, type PollCategory, type PollRoom } from "./api";
import { offeredIn } from "./choices";

const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
/** A new voting lobby starts with two seats, as today; the owner widens it in the lobby. */
const DEFAULT_CAPACITY = 2;

/** Today's voting setup: language, length and categories; it creates a lobby, or changes an open one (?lobby=id). */
export function VotingSettings() {
  const { t } = useApp();
  const me = usePlayer();
  const { route, query } = useLocation();
  const lobbyId = query.lobby ?? null;
  const back = destination(query.return);
  const [lang, setLang] = useState<string>(t.lang);
  const [questions, setQuestions] = useState(10);
  const [picked, setPicked] = useState<string[]>([]);
  const [categories, setCategories] = useState<PollCategory[] | null>(null);
  const [lobby, setLobby] = useState<PollRoom | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!me) return;
    void (async () => {
      const [offered, room] = await Promise.all([
        get<PollCategory[]>("/f/polls/categories").catch(() => []),
        lobbyId ? get<PollRoom>(`${POLL_API}/${lobbyId}`).catch(() => null) : Promise.resolve(null),
      ]);
      const settings = room?.settings;
      const startLang = settings?.lang ?? t.lang;
      setLobby(room);
      if (settings) {
        setLang(settings.lang);
        setQuestions(settings.questions);
      }
      const offeredNow = offeredIn(offered, startLang).map((c) => c.id);
      setPicked((settings?.categoryIds ?? []).filter((id) => offeredNow.includes(id)));
      setCategories(offered);
    })();
  }, [me?.id]);

  if (!me || categories === null) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  const shown = offeredIn(categories, lang);
  const pickLang = (next: string) => {
    setLang(next);
    const ids = offeredIn(categories, next).map((c) => c.id);
    setPicked(picked.filter((id) => ids.includes(id)));
  };
  const toggle = (id: string) => setPicked(picked.includes(id) ? picked.filter((c) => c !== id) : [...picked, id]);
  const name = (c: PollCategory) => (lang === "fa" ? c.nameFa || c.nameEn : lang === "nl" ? c.nameNl || c.nameEn : c.nameEn || c.nameFa);

  const done = async () => {
    setBusy(true);
    setError(null);
    const settings = { lang, questions, categoryIds: picked };
    try {
      if (lobbyId) {
        if (!lobby) throw new ApiError(404, "not_found", null);
        await put(`${POLL_API}/${lobbyId}/settings`, { ...settings, capacity: lobby.capacity });
        route(back);
      } else {
        const created = await post<{ id: string; code: string }>(`${POLL_API}/lobby`, { ...settings, capacity: DEFAULT_CAPACITY });
        route(`/voting/lobby/${created.code}`);
      }
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setBusy(false);
      setError(t.t("common.error"));
    }
  };

  return (
    <div class="shell">
      <header class="topbar">
        <a class="icon-hit icon-hit--back" href={back} aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
        <h1 class="topbar__title">{t.t("voting.settings.title")}</h1>
      </header>

      <section class="sheet enter">
        <p class="muted" style="margin:0 0 1rem">{t.t("voting.settings.summary")}</p>

        <div class="script-tiles" role="group" aria-label={t.t("lang.pick")}>
          {LANGS.map((code: Lang) => (
            <button type="button" class={`script-tile${lang === code ? " is-on" : ""}`} aria-pressed={lang === code} disabled={busy} onClick={() => pickLang(code)}>
              {t.t(`lang.${code}`)}
            </button>
          ))}
        </div>

        <div class="count-row" role="group" aria-label={t.t("home.length")} style="margin-top:1rem">
          {QUESTION_COUNTS.map((count) => (
            <button type="button" class={`count-row__option${questions === count ? " is-on" : ""}`} aria-pressed={questions === count} disabled={busy} onClick={() => setQuestions(count)}>
              {t.num(count)}
            </button>
          ))}
        </div>

        <div class="row row--between" style="margin-top:1.1rem;align-items:baseline">
          <span class="muted">{t.t("voting.lobby.categories")}</span>
          {picked.length > 0 && <button type="button" class="any-pill" disabled={busy} onClick={() => setPicked([])}>{t.t("voting.lobby.anyCategories")}</button>}
        </div>
        {shown.length > 0 && (
          <div class="topic-grid" style="margin-top:.55rem">
            {shown.map((category) => {
              const selected = picked.includes(category.id);
              return (
                <button type="button" class={`topic-tile${selected ? " is-on" : ""}`} aria-pressed={selected} disabled={busy} onClick={() => toggle(category.id)}>
                  <span class="topic-tile__icon" aria-hidden="true">{category.icon}</span>
                  <span class="topic-tile__name" dir="auto">{name(category)}</span>
                  {selected && <span class="topic-tile__check" aria-hidden="true">✓</span>}
                </button>
              );
            })}
          </div>
        )}

        {error && <div class="notice notice--bad" style="margin-top:1rem">{error}</div>}
        <button type="button" class="btn btn--done" disabled={busy} onClick={() => void done()}>
          {busy ? t.t("common.loading") : lobbyId ? t.t("questions.done") : t.t("voting.settings.create")}
        </button>
      </section>
    </div>
  );
}
