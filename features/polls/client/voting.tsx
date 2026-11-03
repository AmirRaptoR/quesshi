import { useEffect, useState } from "preact/hooks";
import { ApiError, post } from "../../../client/api";
import { useApp } from "../../../client/app";
import { initial, tint } from "../../../client/avatar";
import { Icon } from "../../../client/components/icon";
import { MediaBlock } from "../../../client/components/media-block";
import type { PageProps } from "../../../client/feature";
import type { Seat } from "../../../client/lobby";
import { usePlayer } from "../../../client/player";
import { useRoom } from "../../../client/room";
import { GUEST_ROUTE_KEY, stored } from "../../../client/storage";
import { POLL_API, type PollRoom } from "./api";
import { agreementKey, answerText, optionText, pairAgreement, type PollOption } from "./results";

/** Today's voting screen: everyone answers each question, then every answer and the agreement at the end. */
export function Voting({ params }: PageProps) {
  const { t } = useApp();
  const me = usePlayer(true);
  const id = params.id ?? "";
  const { room, reload } = useRoom<PollRoom>(POLL_API, me ? id : null);
  const [answering, setAnswering] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!me?.guest || !room) return;
    // A guest's one game is this one until it ends; then the front door has nothing to send them back to.
    if (room.phase === "ended") stored.remove(GUEST_ROUTE_KEY);
    else if (room.phase === "running") stored.set(GUEST_ROUTE_KEY, `/voting/${id}`);
  }, [room?.phase]);

  if (!me || room === undefined) return <div class="shell game-shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  if (room === null) {
    return <div class="shell game-shell"><div class="empty"><p>{t.t("common.error")}</p><a class="btn btn--ghost" href="/play">{t.t("common.back")}</a></div></div>;
  }
  if (room.phase === "lobby") {
    return (
      <div class="shell game-shell">
        <div class="empty"><p>{t.t("voting.play.waitingForStart")}</p><a class="btn btn--block" href={`/voting/lobby/${room.code}`}>{t.t("voting.play.openLobby")}</a></div>
      </div>
    );
  }

  const players = room.participants;
  const inactive = room.inactive ?? [];
  const question = room.phase === "running" ? room.question ?? null : null;
  const total = question?.total ?? room.review?.length ?? room.settings.questions;
  const shown = (question?.slot ?? total - 1) + 1;
  const progress = total === 0 ? 0 : Math.floor((100 * shown) / total);
  const who = (player: string) => (player === me.id ? t.t("result.you") : players.find((p) => p.id === player)?.displayName ?? player);
  const nameOf = (player: string) => players.find((p) => p.id === player)?.displayName ?? player;
  const face = (seat: Seat | undefined, fallback: string) => (
    <span class="avatar avatar--sm" style={`background:${tint(seat?.avatarSeed ?? fallback)}`}>{initial(seat?.displayName ?? "")}</span>
  );
  const optionFace = (option: PollOption) => (option.kind === "participant" ? face(players.find((p) => p.id === option.player), option.player) : null);

  const answer = async (slot: number, option: number) => {
    setAnswering(true);
    setError(null);
    try {
      await post(`${POLL_API}/${id}/answer`, { slot, option });
      await reload();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("common.error"));
    } finally {
      setAnswering(false);
    }
  };

  const finished = room.phase === "ended";
  const results = room.results;
  const unanswered = question ? players.filter((p) => !inactive.includes(p.id) && !(room.answered ?? []).includes(p.id)) : [];

  return (
    <div class="shell game-shell">
      <header class="topbar game-hud enter">
        <a class="lobby-back" href="/play" aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
        <h1 class="topbar__title">{t.t("voting.play.title")}</h1>
        <div class="topbar__spacer" />
        <span class="muted numeral">{t.num(shown)} / {t.num(total)}</span>
        <span class="game-progress" style={`--game-progress:${progress}%`} aria-hidden="true"><i /></span>
      </header>

      <section class="voting-players enter" aria-label={t.t("voting.play.players")}>
        {players.map((player) => (
          <div class={`voting-player${inactive.includes(player.id) ? " is-left" : ""}`} key={player.id}>
            {face(player, player.id)}
            <span class="voting-player__name" dir="auto">{who(player.id)}</span>
            {inactive.includes(player.id) && <span class="faint">{t.t("voting.play.left")}</span>}
          </div>
        ))}
      </section>

      {question && (
        <>
          <section class="card card--taq game-stage" key={question.slot}>
            <p class="game-kicker">{t.t("voting.play.choose")} <span class="numeral">{t.num(question.slot + 1)}</span></p>
            <h1 dir="auto" style="font-size:var(--step-2);text-wrap:balance">{question.prompt}</h1>
            <MediaBlock media={question.media} loop={false} autoplay={false} />
            <div class="stack game-answers" style="margin-top:1rem;gap:.5rem">
              {question.options.map((option, index) => {
                const selected = room.mine === index;
                return (
                  <button type="button" class={`answer game-answer${selected ? " answer--selected" : ""}`} style={`--answer-i:${index}`} aria-pressed={selected}
                    disabled={answering} onClick={() => void answer(question.slot, index)}>
                    <span class="answer__key" aria-hidden="true">{t.num(index + 1)}</span>
                    {optionFace(option)}
                    <span class="grow" dir="auto">{optionText(option, players, t)}</span>
                  </button>
                );
              })}
            </div>
          </section>

          {room.mine !== null && room.mine !== undefined ? (
            <section class="game-status enter" style="margin-top:.8rem;text-align:center">
              {t.t("voting.play.waiting")}
              <span class="faint" style="display:block;margin-top:.25rem">{t.format("voting.play.answered", (room.answered ?? []).length, players.length)}</span>
              {unanswered.length > 0 && (
                <span class="faint" style="display:block;margin-top:.25rem">{t.format("voting.play.waitingFor", unanswered.map((p) => who(p.id)).join(", "))}</span>
              )}
            </section>
          ) : (
            <p class="faint enter" style="text-align:center;margin-top:.8rem">{t.t("voting.play.answerHint")}</p>
          )}
        </>
      )}

      {finished && (room.review ?? []).map((closed, slot) => {
        const answers = room.answers?.[slot] ?? {};
        const result = results?.slots[slot] ?? null;
        const notApplicable = closed.options.findIndex((option) => option.kind === "not_applicable");
        return (
          <section class="card card--flat game-review enter" style="margin-top:1rem" key={slot}>
            <div class="row row--between"><strong>{t.t("voting.play.previous")}</strong><span class="muted">{t.num(slot + 1)}</span></div>
            <p dir="auto" style="margin:.5rem 0 .7rem">{closed.prompt}</p>
            <MediaBlock media={closed.media} loop={false} autoplay={false} />
            <div class="stack" style="gap:.35rem">
              {players.map((player) => (
                <div class="row" style="gap:.5rem;font-size:var(--step--1)">
                  {face(player, player.id)}
                  <span class="grow" dir="auto">{who(player.id)}</span>
                  <span>{answerText(closed.options, answers[player.id], players, t)}</span>
                </div>
              ))}
            </div>
            {result && (
              <>
                <p class="muted" style="margin:.8rem 0 0">{t.t(agreementKey(result, notApplicable < 0 ? null : notApplicable, notApplicable >= 0 && answers[me.id] === notApplicable))}</p>
                <div class="voting-distribution" aria-label={t.t("voting.play.distribution")}>
                  {closed.options.map((option, index) => (
                    <div class="row row--between voting-distribution__row">
                      <span class="row" style="gap:.4rem" dir="auto">{optionFace(option)}<span>{optionText(option, players, t)}</span></span>
                      <span class="numeral">{t.num(result.counts[index] ?? 0)}</span>
                    </div>
                  ))}
                </div>
              </>
            )}
          </section>
        );
      })}

      {finished && (
        <section class="card card--taq game-stage game-results" style="margin-top:1rem;text-align:center">
          {room.endedReason !== "resolved" ? (
            <h2>{t.t("voting.results.noContest")}</h2>
          ) : (
            <>
              <h2>{t.t("voting.results.title")}</h2>
              {results?.pairs?.length ? (
                <div class="stack" style="margin-top:.8rem;gap:.45rem">
                  {results.pairs.map((pair) => (
                    <div class="row row--between"><span dir="auto">{nameOf(pair.players[0])} · {nameOf(pair.players[1])}</span><span>{pairAgreement(pair.percent, t)}</span></div>
                  ))}
                </div>
              ) : results?.allAgreedCount !== null && results?.allAgreedCount !== undefined ? (
                <p class="muted">{t.format("voting.results.allAgreed", results.allAgreedCount, total)}</p>
              ) : null}
            </>
          )}
          <a class="btn btn--ghost btn--block" style="margin-top:1rem" href="/play">{t.t("result.again")}</a>
        </section>
      )}

      {error && <div class="notice notice--bad enter">{error}</div>}
    </div>
  );
}
