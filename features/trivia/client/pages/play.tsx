import { useEffect, useRef, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { get, post } from "../../../../client/api";
import { MediaBlock } from "../../../../client/components/media-block";
import type { PageProps } from "../../../../client/feature";
import { GameHud, KEYS } from "../components/game";
import { MapField } from "../components/map-field";
import { MapReveal } from "../components/map-reveal";
import { ReportQuestion } from "../components/report-question";
import { Ring } from "../components/ring";
import { SortList } from "../components/sort-list";
import { SortReveal } from "../components/sort-reveal";
import { textsOf } from "../logic/answer-reveal";
import { remainingSeconds } from "../logic/clock";
import type { MapPick } from "../logic/map";
import { served, type Ordering } from "../logic/sort-ordering";
import { play } from "../sounds";
import type { Answered, ChoiceCard, DuelRoomView, MapCard, MapTarget, OrderCard, Served } from "../types";

/**
 * Today's asynchronous duel question: one card at a time on the player's own clock, its answer
 * shown at once, then the next. A choice question colours its own tiles; a sorting or map question
 * swaps its control for the reveal, since the answer cannot be drawn on top of the control.
 */
export function Play({ params }: PageProps) {
  const { me, t } = useApp();
  const { route } = useLocation();
  const id = params.id ?? "";
  const [card, setCard] = useState<Served | null>(null);
  const [failed, setFailed] = useState(false);
  const [result, setResult] = useState<Answered | null>(null);
  const [chosen, setChosen] = useState<number | null>(null);
  const [timedOut, setTimedOut] = useState(false);
  const [ordering, setOrdering] = useState<Ordering>([]);
  const [pick, setPick] = useState<MapPick | null>(null);
  const [endsAt, setEndsAt] = useState(0);
  const [now, setNow] = useState(Date.now());
  const answering = useRef(false);
  // The clock stops where it was when the answer went in.
  const [stopped, setStopped] = useState<number | null>(null);
  const meId = me?.id ?? "";

  const next = async () => {
    setResult(null);
    setTimedOut(false);
    setChosen(null);
    setPick(null);
    setStopped(null);
    answering.current = false;
    try {
      const served_ = await post<Served>(`/f/trivia/duel/${id}/next`);
      setCard(served_);
      setOrdering(served((served_.question.card as Partial<OrderCard>).items?.length ?? 0));
      // The clock runs on this phone, as today, so a phone whose clock is off still gets its full time.
      setEndsAt(Date.now() + served_.limitMs);
    } catch {
      // Nothing left to serve: this player's run is done.
      route(`/duel/${id}`, true);
    }
  };

  useEffect(() => {
    void next();
    const timer = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(timer);
  }, [id]);

  const answer = async (value: unknown, choice: number | null = null, clockRanOut = false) => {
    if (!card || answering.current) return;
    answering.current = true;
    setStopped(remainingSeconds(endsAt, Date.now()));
    setChosen(choice);
    setTimedOut(clockRanOut);
    try {
      const answered = await post<Answered>(`/f/trivia/duel/${id}/answer`, { slot: card.slot, answer: value });
      setResult(answered);
      play(answered.correct ? "correct" : "wrong");
      if (answered.runFinished) void cueOutcome();
    } catch {
      setFailed(true);
    }
  };
  // The run being over is not the duel being over; only a settled duel earns a cue.
  const cueOutcome = async () => {
    const view = await get<DuelRoomView>(`/f/trivia/duel/${id}`).catch(() => null);
    const outcome = view?.standings?.find((s) => s.player === meId)?.outcome;
    if (outcome === "win") play("win");
    if (outcome === "loss") play("loss");
  };

  const remaining = card ? remainingSeconds(endsAt, now) : 0;
  useEffect(() => {
    // Only the clock may send an empty answer.
    if (card && !result && remaining === 0 && !answering.current) void answer(null, null, true);
  }, [remaining]);

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
  if (!card) return <div class="shell game-shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  const question = card.question;
  const done = result !== null;
  let surface;
  if (question.kind === "order") {
    const items = (question.card as OrderCard).items;
    const mine = timedOut ? null : ordering.map((i) => items[i]!.text);
    surface = result ? (
      <SortReveal correct={textsOf(items, (result.reveal as { itemIds: string[] }).itemIds) ?? []} columns={[{ label: t.t("result.you"), placed: mine, answered: true }]} />
    ) : (
      <>
        <SortList items={items.map((item) => item.text)} ordering={ordering} onChange={setOrdering} disabled={done} />
        <button class="btn btn--block" style="margin-top:.9rem" disabled={done} onClick={() => void answer({ itemIds: ordering.map((i) => items[i]!.id) })}>{t.t("play.lockIn")}</button>
      </>
    );
  } else if (question.kind === "map_pick") {
    const map = question.card as MapCard;
    surface = result ? (
      <MapReveal target={(result.reveal as { target: MapTarget }).target} baseLayer={map.baseLayer}
        answers={[{ label: t.t("result.you"), pick: timedOut ? null : pick, right: result.correct, answered: true }]} />
    ) : (
      <MapField shape={map.shape} baseLayer={map.baseLayer} selection={pick} onSelect={setPick} disabled={done} onConfirm={() => pick && void answer(pick)} />
    );
  } else {
    const correctIndex = (result?.reveal as { correctIndex?: number } | undefined)?.correctIndex;
    const tone = (index: number) => {
      if (!result) return "";
      if (index === correctIndex) return " answer--right";
      return !result.correct && !timedOut && index === chosen ? " answer--wrong" : " answer--dim";
    };
    surface = (
      <div class="answers game-answers">
        {(question.card as ChoiceCard).options.map((option, index) => (
          <button class={`answer game-answer${tone(index)}`} style={`--answer-i:${index}`} disabled={done} onClick={() => void answer({ index }, index)}>
            <span class="answer__key" aria-hidden="true">{KEYS[index]}</span>
            <span class="grow" dir="auto">{option}</span>
            {result && index === correctIndex && <span aria-hidden="true">✓</span>}
          </button>
        ))}
      </div>
    );
  }

  return (
    <div class="shell game-shell">
      <GameHud question={question} slot={card.slot} total={card.total}
        clock={<Ring remaining={stopped ?? remaining} total={Math.round(card.limitMs / 1000)} size="38" />} />
      <section class="card card--taq game-stage" key={card.slot}>
        <p class="game-kicker">{t.t("play.question")} <span class="numeral">{t.num(card.slot + 1)}</span></p>
        <h1 dir="auto" style="font-size:var(--step-2);text-wrap:balance">{question.prompt}</h1>
        <MediaBlock media={question.media} />
        {surface}
      </section>
      {result && (
        <section class="card card--flat game-feedback enter" style="margin-top:.85rem">
          <div class="row row--between">
            <strong style={`color:${result.correct ? "var(--zaferan)" : "var(--anaar)"}`}>
              {t.t(timedOut ? "play.timeUp" : result.correct ? "play.correct" : "play.wrong")}
            </strong>
            <span class="numeral" style="color:var(--zaferan)">+{t.num(result.points)}</span>
          </div>
          {result.explanation?.trim() && <p class="muted" dir="auto" style="margin:.6rem 0 0;font-size:var(--step--1)">{result.explanation}</p>}
          <button class="btn btn--block" style="margin-top:.9rem" onClick={() => (result.runFinished ? route(`/duel/${id}`) : void next())}>
            {t.t(result.runFinished ? "play.finish" : "play.next")}
          </button>
          <div style="margin-top:.7rem;text-align:center"><ReportQuestion questionId={result.questionId} /></div>
        </section>
      )}
    </div>
  );
}
