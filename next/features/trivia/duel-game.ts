import type { Clock } from "../../server/core/clock";
import { MalformedAnswer } from "../../server/core/kind";
import type { Schedule } from "../../server/core/schedule";
import type { SessionsStore } from "../../server/core/sessions.store";
import { asyncDuel, type DuelState } from "./formats/async-duel";
import { kinds, type TriviaItem } from "./items";

const LIMIT_MS = 20_000;
const FORFEIT_MS = 48 * 3600_000;

interface Deps {
  sessionId: string;
  players: string[];
  items: TriviaItem[];
  createdAt: number;
  clock: Clock;
  schedule: Schedule;
  random: () => number;
  sessions: SessionsStore;
  onChange: () => void;
}

/** One running asynchronous duel: each player plays the same questions at their own pace. */
export class DuelGame {
  readonly #deps: Deps;
  readonly #resolved: unknown[];
  #state: DuelState;
  #cancelForfeit: () => void;
  /** What each player answered, by slot, for the review at the end of their run. */
  readonly #given = new Map<string, Map<number, { value: unknown; correct: boolean }>>();

  constructor(deps: Deps) {
    this.#deps = deps;
    this.#resolved = deps.items.map((item) => kinds[item.kind].resolve(item.payload, deps.random));
    this.#state = asyncDuel.start({ players: deps.players, levels: deps.items.map((item) => item.level), createdAt: deps.createdAt });
    this.#cancelForfeit = deps.schedule(deps.createdAt + FORFEIT_MS, () => this.#apply(asyncDuel.settle(this.#state, this.#now())));
  }

  get ended() {
    return this.#state.ended;
  }

  /** An administrator ends the duel as a no contest. */
  end(): void {
    if (!this.#state.ended) this.#apply(asyncDuel.abort(this.#state));
  }

  /** The player's open question, or their next one. Null when they have nothing left to play. */
  next(player: string) {
    const before = this.#state;
    const { state, result } = asyncDuel.next(before, { type: "serve", player, at: this.#now() });
    this.#recordMisses(before, state, player);
    this.#apply(state);
    if (result.type !== "served") return null;
    const item = this.#deps.items[result.slot]!;
    return {
      slot: result.slot, total: this.#deps.items.length, servedAt: result.servedAt, limitMs: LIMIT_MS,
      question: { id: item.id, categoryId: item.categoryId, prompt: item.prompt, kind: item.kind, level: item.level, media: item.media, card: kinds[item.kind].card(item.payload, this.#resolved[result.slot]) },
    };
  }

  /** A null answer means the player's clock ran out. */
  answer(player: string, slot: number, raw: unknown) {
    const item = this.#deps.items[slot];
    if (!item) return { error: "malformed_answer" as const };
    const kind = kinds[item.kind];
    let stored: unknown = null;
    let credit = 0;
    if (raw !== null) {
      try {
        stored = kind.normalize(item.payload, this.#resolved[slot], raw);
      } catch (error) {
        if (error instanceof MalformedAnswer) return { error: "malformed_answer" as const };
        throw error;
      }
      credit = kind.grade(item.payload, this.#resolved[slot], stored);
    }
    const { state, result } = asyncDuel.next(this.#state, { type: "answer", player, slot, credit, at: this.#now() });
    if (result.type !== "answered") {
      this.#apply(state);
      return { error: "not_accepted" as const };
    }
    this.#deps.sessions.recordAnswer({
      sessionId: this.#deps.sessionId, playerId: player, slot, value: stored, credit, points: result.points,
      answeredAt: new Date(this.#now()).toISOString(),
    });
    this.#givenBy(player).set(slot, { value: stored, correct: credit === 1 });
    this.#apply(state);
    const run = state.runs[player]!;
    return {
      slot, questionId: item.id, points: result.points, correct: credit === 1, reveal: kind.reveal(item.payload, this.#resolved[slot]), explanation: item.explanation,
      runFinished: run.answers.length >= this.#deps.items.length, runScore: run.answers.reduce((sum, a) => sum + a.points, 0),
    };
  }

  /** What one player may see: everyone's progress, others' scores only once their own run is done. */
  view(viewer: string) {
    const state = this.#state;
    const viewerDone = (state.runs[viewer]?.answers.length ?? 0) >= this.#deps.items.length;
    return {
      total: this.#deps.items.length,
      runs: state.players.map((player) => {
        const run = state.runs[player]!;
        const visible = player === viewer || viewerDone || state.ended !== null;
        return { player, answered: run.answers.length, finished: run.answers.length >= this.#deps.items.length, score: visible ? run.answers.reduce((sum, a) => sum + a.points, 0) : null };
      }),
      standings: state.ended?.standings ?? null,
      review: viewerDone || state.ended !== null ? this.#review() : null,
    };
  }

  /** Every question with its answer and the answers given so far, in seat order. */
  #review() {
    return this.#deps.items.map((item, slot) => ({
      slot, questionId: item.id, categoryId: item.categoryId, prompt: item.prompt, kind: item.kind, level: item.level, media: item.media, explanation: item.explanation,
      card: kinds[item.kind].card(item.payload, this.#resolved[slot]),
      reveal: kinds[item.kind].reveal(item.payload, this.#resolved[slot]),
      answers: this.#state.players.flatMap((player) => {
        const given = this.#given.get(player)?.get(slot);
        return given ? [{ player, ...given }] : [];
      }),
    }));
  }

  #givenBy(player: string) {
    let given = this.#given.get(player);
    if (!given) this.#given.set(player, (given = new Map()));
    return given;
  }

  #now() {
    return this.#deps.clock.now().getTime();
  }

  #recordMisses(before: DuelState, after: DuelState, player: string) {
    const known = before.runs[player]?.answers.length ?? 0;
    for (const missed of after.runs[player]?.answers.slice(known) ?? []) {
      this.#deps.sessions.recordAnswer({ sessionId: this.#deps.sessionId, playerId: player, slot: missed.slot, value: null, credit: 0, points: 0, answeredAt: null });
      this.#givenBy(player).set(missed.slot, { value: null, correct: false });
    }
  }

  #apply(state: DuelState) {
    const wasEnded = this.#state.ended !== null;
    this.#state = state;
    if (!wasEnded && state.ended) {
      this.#cancelForfeit();
      this.#deps.sessions.finish({ sessionId: this.#deps.sessionId, endedAt: new Date(this.#now()).toISOString(), reason: state.ended.reason, result: state.ended });
    }
    this.#deps.onChange();
  }
}
