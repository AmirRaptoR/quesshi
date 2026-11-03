import { useEffect, useRef, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { ApiError, get, post } from "../../../../client/api";
import { MediaBlock } from "../../../../client/components/media-block";
import { Shamseh } from "../../../../client/components/shamseh";
import type { PageProps } from "../../../../client/feature";
import { DuelBar, type DuelBarSide } from "../components/duel-bar";
import { GameHud, KEYS } from "../components/game";
import { MapField } from "../components/map-field";
import { MapReveal } from "../components/map-reveal";
import { Ring } from "../components/ring";
import { SortList } from "../components/sort-list";
import { SortReveal } from "../components/sort-reveal";
import { StandingsList } from "../components/standings-list";
import { initial, tint } from "../../../../client/avatar";
import { textsOf } from "../logic/answer-reveal";
import { remainingSeconds, shouldTick } from "../logic/clock";
import { headlineKey } from "../logic/outcome";
import type { MapPick } from "../logic/map";
import { served, type Ordering } from "../logic/sort-ordering";
import { play } from "../sounds";
import { roomSocket } from "../socket";
import type { ChoiceCard, LiveGameView, LivePlayer, LiveRoomView, MapCard, MapTarget, OrderCard, Participant } from "../types";

/** Today's time to answer; the ring's denominator, while the server's deadline ends the question. */
const QUESTION_SECONDS = 20;

type RoundAnswers = NonNullable<LiveGameView["reveal"]>["answers"];

/**
 * Today's live game screen: a countdown, then each question for everyone at once, its reveal, and
 * the results. Broadcasts leave out the roster once the game runs and the per-player list while a
 * question is open, so the screen keeps the last of each it saw and reads the answered count instead.
 */
export function Live({ params }: PageProps) {
  const { me, t } = useApp();
  const { route } = useLocation();
  const meId = me?.id ?? "";
  const id = params.id ?? "";
  const [room, setRoom] = useState<LiveRoomView | null>(null);
  const [refused, setRefused] = useState(false);
  const [reconnecting, setReconnecting] = useState(false);
  const [now, setNow] = useState(Date.now());
  const [locked, setLocked] = useState(false);
  const [myChoice, setMyChoice] = useState<number | null>(null);
  const [ordering, setOrdering] = useState<Ordering>([]);
  const [pick, setPick] = useState<MapPick | null>(null);
  const [rematchPressed, setRematchPressed] = useState(false);
  const [rematchFailed, setRematchFailed] = useState(false);
  // Every round's answers, kept from the reveals, so the results can draw the rays.
  const [rounds, setRounds] = useState<Map<number, RoundAnswers>>(new Map());
  const socket = useRef<ReturnType<typeof roomSocket> | null>(null);
  const lastTicked = useRef<number | null>(null);
  const cued = useRef<string>("");

  const take = (next: LiveRoomView) =>
    setRoom((previous) => {
      const game = next.game ?? null;
      // Batched broadcasts may skip views but never reorder them; an older one is ignored.
      if (previous?.game && game && game.version < previous.game.version) return previous;
      return {
        ...next,
        participants: next.participants ?? previous?.participants ?? [],
        game: game && { ...game, players: game.players ?? previous?.game?.players ?? [] },
      };
    });

  useEffect(() => {
    get<LiveRoomView>(`/f/trivia/live/${id}`).then(
      (first) => {
        if (first.phase === "lobby") return route(`/lobby/${first.code}`, true);
        take(first);
      },
      (failure) => { if (failure instanceof ApiError) setRefused(true); },
    );
    socket.current = roomSocket(`/f/trivia/live/${id}/socket`, {
      message(data) {
        if (data.type === "view") take(data.view as LiveRoomView);
        if (data.type === "answered") setLocked(true);
      },
      connected: (up) => setReconnecting(!up),
    });
    const timer = setInterval(() => setNow(Date.now()), 250);
    return () => {
      clearInterval(timer);
      socket.current?.close();
    };
  }, [id]);

  const game = room?.game ?? null;
  const people: Participant[] = room?.participants ?? [];
  const players: LivePlayer[] = game?.players ?? [];
  const mineSeat = players.find((p) => p.player === meId);
  const eliminated = mineSeat?.status === "eliminated" || mineSeat?.status === "left";

  // A new question resets the answer being built; a reveal is kept for the rays; the ending cues the result.
  useEffect(() => {
    if (!game) return;
    const key = `${game.phase}:${game.slot}`;
    if (cued.current === key) return;
    cued.current = key;
    if (game.phase === "question") {
      setLocked(false);
      setMyChoice(null);
      setPick(null);
      const card = game.question?.card as Partial<OrderCard> | undefined;
      setOrdering(served(card?.items?.length ?? 0));
      lastTicked.current = null;
      play("roundStart");
    } else if (game.phase === "reveal" && game.reveal) {
      setRounds((previous) => new Map(previous).set(game.slot, game.reveal!.answers));
      const mine = game.reveal.answers.find((a) => a.player === meId);
      if (mine && mine.value !== null) play(mine.points > 0 ? "correct" : "wrong");
    } else if (game.phase === "ended") {
      const outcome = game.standings?.find((s) => s.player === meId)?.outcome;
      if (outcome === "win") play("win");
      if (outcome === "loss") play("loss");
    }
  }, [game?.phase, game?.slot]);

  // Everyone in the game is sent to the rematch lobby, as today.
  useEffect(() => {
    if (room?.rematch) route(`/lobby/${room.rematch.code}`);
  }, [room?.rematch?.code]);

  const timeUp = game?.phase === "question" && game.questionEndsAt !== null && now >= game.questionEndsAt;
  useEffect(() => {
    if (game?.phase !== "question" || game.questionEndsAt === null) return;
    const remaining = remainingSeconds(game.questionEndsAt, now);
    if (shouldTick(remaining, lastTicked.current)) {
      lastTicked.current = remaining;
      play("tick");
    }
  }, [now]);

  if (refused || (!room && reconnecting)) {
    return (
      <div class="shell game-shell">
        <div class="empty">
          <p>{t.t(refused ? "live.refused" : "common.error")}</p>
          <a class="btn btn--ghost" href="/">{t.t("common.back")}</a>
        </div>
      </div>
    );
  }
  if (!room || !game) return <div class="shell game-shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  const person = (playerId: string) => people.find((p) => p.id === playerId);
  const nameOf = (playerId: string) => person(playerId)?.displayName ?? "";
  const others = people.filter((p) => p.id !== meId);
  const side = (p: Participant): DuelBarSide => {
    const seat = players.find((s) => s.player === p.id);
    return { id: p.id, name: p.displayName, avatar: p.avatarSeed, score: seat?.score ?? 0, out: seat?.status === "eliminated" || seat?.status === "left" };
  };
  const bar = <DuelBar mine={side(person(meId) ?? { id: meId, displayName: me?.displayName ?? "", avatarSeed: me?.avatarSeed ?? meId, guest: false })} others={others.map(side)} />;
  const closed = locked || timeUp || eliminated;

  const answer = (value: unknown, choice: number | null = null) => {
    if (closed || !game.question) return;
    setLocked(true);
    setMyChoice(choice);
    // A late or repeated answer is refused; the reveal that follows is what counts.
    socket.current?.send({ type: "answer", slot: game.slot, answer: value });
  };
  const rematch = async () => {
    setRematchPressed(true);
    setRematchFailed(false);
    try {
      const created = await post<{ id: string; code: string }>(`/f/trivia/live/${id}/rematch`);
      route(`/lobby/${created.code}`);
    } catch {
      setRematchPressed(false);
      setRematchFailed(true);
    }
  };

  let body;
  if (game.phase === "countdown") {
    body = (
      <section class="card card--taq game-stage game-countdown" style="text-align:center">
        <div class="row row--between" style="margin-top:.5rem;flex-wrap:wrap;justify-content:center;gap:1rem">
          {people.map((p) => (
            <div class="stack" style="justify-items:center;gap:.35rem" key={p.id}>
              <span class="avatar avatar--lg" style={`background:${tint(p.avatarSeed)}`}>{initial(p.displayName)}</span>
              <span dir="auto">{p.displayName}</span>
            </div>
          ))}
        </div>
        <p class="faint" style="margin-top:1rem">{t.t("live.countdown.starting")}</p>
        <p class="numeral" style="font-size:var(--step-4);margin-top:.5rem">{t.num(remainingSeconds(game.dueAt, now))}</p>
      </section>
    );
  } else if (game.phase === "question" && game.question) {
    const question = game.question;
    // Everyone still playing has answered, me included once I have.
    const answered = game.answeredCount ?? 0;
    const active = game.activeCount ?? 0;
    const othersAnswered = active > 1 && answered >= (locked ? active : active - 1);
    body = (
      <>
        {bar}
        <GameHud question={question} slot={game.slot} total={game.total}
          clock={game.questionEndsAt !== null && <Ring endsAt={game.questionEndsAt} total={QUESTION_SECONDS} size="38" />} />
        <section class="card card--taq game-stage" key={game.slot}>
          <p class="game-kicker">{t.t("play.question")} <span class="numeral">{t.num(game.slot + 1)}</span></p>
          <h1 dir="auto" style="font-size:var(--step-2);text-wrap:balance">{question.prompt}</h1>
          <MediaBlock media={question.media} />
          {question.kind === "order" ? (
            <>
              <SortList items={(question.card as OrderCard).items.map((item) => item.text)} ordering={ordering} onChange={setOrdering} disabled={closed} />
              <button class="btn btn--block" style="margin-top:.9rem" disabled={closed}
                onClick={() => answer({ itemIds: ordering.map((i) => (question.card as OrderCard).items[i]!.id) })}>{t.t("play.lockIn")}</button>
            </>
          ) : question.kind === "map_pick" ? (
            <MapField shape={(question.card as MapCard).shape} baseLayer={(question.card as MapCard).baseLayer} selection={pick} onSelect={setPick}
              disabled={closed} onConfirm={() => pick && answer(pick)} />
          ) : (
            <div class="answers game-answers">
              {(question.card as ChoiceCard).options.map((option, index) => (
                <button class={`answer game-answer${myChoice === index ? " answer--locked" : ""}`} style={`--answer-i:${index}`} disabled={closed} onClick={() => answer({ index }, index)}>
                  <span class="answer__key" aria-hidden="true">{KEYS[index]}</span>
                  <span class="grow" dir="auto">{option}</span>
                </button>
              ))}
            </div>
          )}
        </section>
        <p class="game-status enter" style="margin-top:.7rem">
          {eliminated ? t.t("live.eliminated.notice")
            : locked ? t.t(othersAnswered ? "live.question.theyAnswered" : "live.question.waitingForThem")
            : timeUp ? t.t("play.timeUp")
            : othersAnswered ? t.t("live.question.theyAnswered")
            : t.t("live.question.locked")}
        </p>
      </>
    );
  } else if (game.phase === "reveal" && game.question && game.reveal) {
    const question = game.question;
    const reveal = game.reveal;
    const mine = reveal.answers.find((a) => a.player === meId);
    const theirs = reveal.answers.filter((a) => a.player !== meId);
    const label = (playerId: string) => (playerId === meId ? t.t("result.you") : others.length === 1 ? t.t("result.them") : nameOf(playerId));
    const ranked = [...(mine ? [mine] : []), ...theirs];
    let shown;
    if (question.kind === "order") {
      const items = (question.card as OrderCard).items;
      const correct = textsOf(items, (reveal.answer as { itemIds: string[] }).itemIds) ?? [];
      shown = <SortReveal correct={correct} columns={ranked.map((a) => ({ label: label(a.player), placed: textsOf(items, (a.value as { itemIds?: string[] } | null)?.itemIds), answered: a.value !== null }))} />;
    } else if (question.kind === "map_pick") {
      shown = <MapReveal target={(reveal.answer as { target: MapTarget }).target} baseLayer={(question.card as MapCard).baseLayer}
        answers={ranked.map((a) => ({ label: label(a.player), pick: a.value as MapPick | null, right: a.value === null ? null : a.points > 0, answered: a.value !== null }))} />;
    } else {
      const correctIndex = (reveal.answer as { correctIndex: number }).correctIndex;
      const myIndex = (mine?.value as { index?: number } | null)?.index;
      shown = (
        <>
          <div class="answers game-answers">
            {(question.card as ChoiceCard).options.map((option, index) => (
              <div class="stack" style="gap:.3rem">
                <button class={`answer game-answer ${index === correctIndex ? "answer--right" : index === myIndex ? "answer--wrong" : "answer--dim"}`} disabled>
                  <span class="answer__key" aria-hidden="true">{KEYS[index]}</span>
                  <span class="grow" dir="auto">{option}</span>
                  {index === correctIndex && <span aria-hidden="true">✓</span>}
                </button>
                <div class="row" style="gap:.35rem;flex-wrap:wrap">
                  {myIndex === index && <span class="chip chip--live">{t.t("result.you")}</span>}
                  {theirs.filter((a) => (a.value as { index?: number } | null)?.index === index).map((a) => (
                    <span class="chip" dir="auto" key={a.player}>{label(a.player)}</span>
                  ))}
                </div>
              </div>
            ))}
          </div>
          {ranked.filter((a) => a.value === null).map((a) => (
            <p class="faint" style="margin:.35rem 0 0" key={a.player}><span dir="auto">{label(a.player)}</span>: {t.t("live.noAnswer")}</p>
          ))}
        </>
      );
    }
    body = (
      <>
        {bar}
        <GameHud question={question} slot={game.slot} total={game.total} clock={<Ring endsAt={game.dueAt} total={3} size="38" />} />
        <section class="card card--taq game-stage game-stage--reveal" key={game.slot}>
          <h1 dir="auto" style="font-size:var(--step-2);text-wrap:balance">{question.prompt}</h1>
          <MediaBlock media={question.media} loop={false} autoplay={false} />
          {shown}
        </section>
        <section class="card card--flat enter" style="margin-top:.85rem">
          <div class={others.length <= 1 ? "row row--between" : "row"} style={others.length <= 1 ? undefined : "flex-wrap:wrap;gap:.4rem 1rem"}>
            <span class="numeral" style="color:var(--zaferan)">{t.t("result.you")}: +{t.num(mine?.points ?? 0)}</span>
            {others.length <= 1
              ? <span class="numeral">{t.t("result.them")}: +{t.num(theirs[0]?.points ?? 0)}</span>
              : theirs.map((a) => <span class="numeral" dir="auto" key={a.player}>{nameOf(a.player)}: +{t.num(a.points)}</span>)}
          </div>
          {reveal.explanation?.trim() && <p class="muted" dir="auto" style="margin:.6rem 0 0;font-size:var(--step--1)">{reveal.explanation}</p>}
        </section>
      </>
    );
  } else if (game.phase === "ended" || room.phase === "ended") {
    const rays = (playerId: string) =>
      Array.from({ length: game.total }, (_, slot) => {
        const given = rounds.get(slot)?.find((a) => a.player === playerId);
        return given && given.value !== null ? given.points > 0 : null;
      });
    const two = people.length === 2;
    const canRematch = people.length >= 2 && people.every((p) => !p.guest);
    body = (
      <>
        <section class="card card--taq game-stage game-results" style="text-align:center">
          <h2>{t.t(headlineKey(game.endedReason, game.standings, players, meId))}</h2>
          <div style="width:min(17rem,80%);margin:1.1rem auto .5rem">
            <Shamseh mine={rays(meId)} theirs={two && others[0] ? rays(others[0].id) : []} slots={game.total} ignite hideTheirs={!two}
              centre={t.num(mineSeat?.score ?? 0)} label={t.t("app.name")} />
          </div>
        </section>
        {(game.standings?.length ?? 0) > 0 && (
          <section class="card card--flat enter" style="margin-top:.85rem">
            <h3>{t.t("result.standings")}</h3>
            <div style="margin-top:.5rem"><StandingsList rows={game.standings!} people={people} me={meId} /></div>
          </section>
        )}
        <div class="stack enter" style="margin-top:1.2rem">
          {canRematch ? (
            <>
              {rematchFailed && <p class="notice notice--bad" style="text-align:center">{t.t("live.rematch.failed")}</p>}
              <button class="btn btn--block" disabled={rematchPressed} onClick={() => void rematch()}>{t.t("live.rematch.button")}</button>
            </>
          ) : people.length > 1 && !me?.guest && <p class="faint" style="text-align:center">{t.t("live.rematch.guestBlocked")}</p>}
          {me?.guest ? (
            <>
              <a class="btn btn--ghost btn--block" href="/signin">{t.t("result.keepScore")}</a>
              <p class="faint" style="margin:-.4rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("result.guestHint")}</p>
            </>
          ) : (
            <a class="btn btn--ghost btn--block" href="/">{t.t("result.again")}</a>
          )}
        </div>
      </>
    );
  } else {
    body = <div class="empty"><p>{t.t("common.error")}</p></div>;
  }

  return (
    <div class="shell game-shell">
      {reconnecting && <p class="notice notice--bad enter" role="status">{t.t("live.reconnecting")}</p>}
      {body}
    </div>
  );
}
