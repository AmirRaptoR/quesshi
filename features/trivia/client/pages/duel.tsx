import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { get } from "../../../../client/api";
import { MediaBlock } from "../../../../client/components/media-block";
import { Shamseh } from "../../../../client/components/shamseh";
import type { PageProps } from "../../../../client/feature";
import { useCategories } from "../components/game";
import { MapReveal } from "../components/map-reveal";
import { ReportQuestion } from "../components/report-question";
import { SortReveal } from "../components/sort-reveal";
import { StandingsList } from "../components/standings-list";
import { textsOf } from "../logic/answer-reveal";
import type { MapPick } from "../logic/map";
import type { ChoiceCard, DuelRoomView, MapTarget, OrderCard, ReviewRow } from "../types";

/** Today's duel result: the rosette, the scores or standings, and a review of every question once the player's run is done. */
export function Duel({ params }: PageProps) {
  const { me, t } = useApp();
  const { route } = useLocation();
  const meId = me?.id ?? "";
  const id = params.id ?? "";
  const categories = useCategories();
  const [duel, setDuel] = useState<DuelRoomView | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    get<DuelRoomView>(`/f/trivia/duel/${id}`).then((view) => {
      if (view.phase === "lobby") return route(`/lobby/${view.code}`, true);
      setDuel(view);
    }, () => setFailed(true));
  }, [id]);

  if (failed) {
    return (
      <div class="shell game-shell">
        <div class="empty">
          <p>{t.t("common.error")}</p>
          <a class="btn btn--ghost" href="/">{t.t("common.back")}</a>
        </div>
      </div>
    );
  }
  if (!duel) return <div class="shell game-shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  const people = duel.participants ?? [];
  const others = people.filter((p) => p.id !== meId);
  const opponent = others.length === 1 ? others[0]! : null;
  const runs = duel.runs ?? [];
  const mine = runs.find((r) => r.player === meId);
  const theirs = opponent ? runs.find((r) => r.player === opponent.id) : undefined;
  const settled = duel.phase === "ended";
  const canPlay = duel.phase === "running" && mine !== undefined && !mine.finished;
  const outcome = duel.standings?.find((s) => s.player === meId)?.outcome;
  const headline = t.t(outcome === "win" ? "result.win" : outcome === "loss" ? "result.loss" : outcome === "draw" ? "result.draw" : "result.pending");
  const review = duel.review ?? [];
  const label = (player: string) => (player === meId ? t.t("result.you") : opponent ? t.t("result.them") : (people.find((p) => p.id === player)?.displayName ?? ""));
  const given = (row: ReviewRow, player: string) => row.answers.find((a) => a.player === player);
  // Before the review, the rays only say nothing; afterwards they say right or wrong.
  const rays = (player: string | undefined) => (player ? Array.from({ length: duel.total ?? 0 }, (_, slot) => {
    const answer = review[slot] && given(review[slot], player);
    return answer ? answer.correct : null;
  }) : []);

  return (
    <div class="shell game-shell">
      <section class="card card--taq game-stage game-results" style="text-align:center">
        <h2>{headline}</h2>
        <div style="width:min(17rem,80%);margin:1.1rem auto .5rem">
          <Shamseh mine={rays(meId)} theirs={rays(opponent?.id)} slots={duel.total ?? 0} hideTheirs={!settled} ignite
            centre={settled ? t.num(mine?.score ?? 0) : "…"} label={headline} />
        </div>
        {settled && (duel.standings?.length ?? 0) > 0 ? (
          <div style="margin-top:1rem"><StandingsList rows={duel.standings!} people={people} me={meId} /></div>
        ) : (
          <div class="row row--between" style="margin-top:1rem">
            <div style="text-align:start">
              <div class="faint" style="font-size:var(--step--1)">{t.t("result.you")}</div>
              <div class="numeral" style="font-size:var(--step-2);color:var(--zaferan)">{t.num(mine?.score ?? 0)}</div>
            </div>
            <div style="text-align:end">
              <div class="faint" style="font-size:var(--step--1)">{opponent?.displayName ?? t.t("result.them")}</div>
              <div class="numeral" style="font-size:var(--step-2)">{theirs?.score !== null && theirs?.score !== undefined ? t.num(theirs.score) : "—"}</div>
            </div>
          </div>
        )}
      </section>

      {!settled && <p class="empty enter">{t.t("result.pendingHint")}</p>}

      {review.length > 0 && (
        <section class="enter" style="margin-top:1rem">
          <h3>{t.t("result.review")}</h3>
          <div class="stack" style="margin-top:.6rem">
            {review.map((row) => {
              const category = categories.find((c) => c.id === row.categoryId);
              const answered = [...row.answers.filter((a) => a.player === meId), ...row.answers.filter((a) => a.player !== meId)];
              let shown;
              if (row.kind === "order") {
                const items = (row.card as OrderCard).items;
                shown = <SortReveal correct={textsOf(items, (row.reveal as { itemIds: string[] }).itemIds) ?? []}
                  columns={answered.map((a) => ({ label: label(a.player), placed: textsOf(items, (a.value as { itemIds?: string[] } | null)?.itemIds), answered: true }))} />;
              } else if (row.kind === "map_pick") {
                shown = <MapReveal target={(row.reveal as { target: MapTarget }).target}
                  answers={answered.map((a) => ({ label: label(a.player), pick: a.value as MapPick | null, right: a.correct, answered: true }))} />;
              } else {
                const correctIndex = (row.reveal as { correctIndex: number }).correctIndex;
                shown = (
                  <div class="stack" style="gap:.35rem">
                    {(row.card as ChoiceCard).options.map((option, index) => (
                      <div class="row" dir="auto" style="gap:.5rem;font-size:var(--step--1)">
                        <span style={`color:${index === correctIndex ? "var(--zaferan)" : "var(--sadaf-3)"}`}>{index === correctIndex ? "●" : "○"}</span>
                        <span class={`grow${index === correctIndex ? "" : " faint"}`}>{option}</span>
                        {answered.filter((a) => (a.value as { index?: number } | null)?.index === index).map((a) => (
                          <span class={`chip${a.player === meId ? " chip--live" : ""}`} dir="auto" key={a.player}>{label(a.player)}</span>
                        ))}
                      </div>
                    ))}
                  </div>
                );
              }
              return (
                <article class="card card--flat game-review" key={row.slot}>
                  <div class="row row--between" style="gap:.5rem">
                    <span class="faint" style="font-size:var(--step--1)">{category ? t.name(category) : ""}</span>
                    <span class="faint numeral" style="font-size:var(--step--1)">{t.num(row.slot + 1)}</span>
                  </div>
                  <p dir="auto" style="margin:.4rem 0 .7rem;font-weight:600">{row.prompt}</p>
                  <MediaBlock media={row.media} loop={false} autoplay={false} />
                  {shown}
                  {row.explanation?.trim() && <p class="muted" dir="auto" style="margin:.7rem 0 0;font-size:var(--step--1)">{row.explanation}</p>}
                  <div style="margin-top:.6rem"><ReportQuestion questionId={row.questionId} /></div>
                </article>
              );
            })}
          </div>
        </section>
      )}

      <div class="stack enter" style="margin-top:1.2rem">
        {canPlay && <a class="btn btn--block" href={`/play/${duel.id}`}>{t.t("play.next")}</a>}
        {me?.guest ? (
          <>
            <a class="btn btn--ghost btn--block" href="/signin">{t.t("result.keepScore")}</a>
            <p class="faint" style="margin:-.4rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("result.guestHint")}</p>
          </>
        ) : (
          <a class="btn btn--ghost btn--block" href="/">{t.t("result.again")}</a>
        )}
      </div>
    </div>
  );
}
