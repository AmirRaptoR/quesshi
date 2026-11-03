import type { Clock } from "../../server/core/clock";
import { MalformedAnswer, type Kind } from "../../server/core/kind";
import type { Schedule } from "../../server/core/schedule";
import type { SessionsStore } from "../../server/core/sessions.store";
import { live, type LiveEffect, type LiveEvent, type LiveState, type SeatStatus, type Standing } from "./formats/live";
import { choice } from "./kinds/choice";
import { mapPick } from "./kinds/map-pick";
import { order } from "./kinds/order";

const kinds: Record<TriviaItem["kind"], Kind<unknown, unknown, unknown, unknown, unknown>> = { choice, order, map_pick: mapPick };

export interface TriviaItem {
  prompt: string;
  level: number | null;
  explanation: string | null;
  kind: "choice" | "order" | "map_pick";
  payload: unknown;
}

/** What every phone in the session receives after each change. It never carries an answer before the reveal. */
export interface LiveView {
  version: number;
  phase: LiveState["phase"];
  slot: number;
  total: number;
  dueAt: number;
  questionEndsAt: number | null;
  question: { prompt: string; kind: string; level: number | null; card: unknown } | null;
  reveal: { answer: unknown; explanation: string | null; answers: { player: string; value: unknown; points: number }[] } | null;
  players: { player: string; status: SeatStatus; score: number; answered: boolean }[];
  standings: Standing[] | null;
}

export type AnswerResult = { ok: true } | { ok: false; error: "malformed_answer" | "not_accepted" };

interface Deps {
  sessionId: string;
  players: string[];
  items: TriviaItem[];
  clock: Clock;
  schedule: Schedule;
  random: () => number;
  sessions: SessionsStore;
  publish: (view: LiveView) => void;
}

/** One running live trivia session, in memory. Commands run one at a time to completion, so nothing interleaves. */
export class LiveGame {
  readonly #deps: Deps;
  readonly #resolved: unknown[];
  readonly #rounds = new Map<number, Map<string, { value: unknown; points: number }>>();
  #state!: LiveState;
  #version = 0;
  #cancelTimer: (() => void) | null = null;

  constructor(deps: Deps) {
    this.#deps = deps;
    this.#resolved = deps.items.map((item) => kinds[item.kind].resolve(item.payload, deps.random));
  }

  start(): void {
    const { state, effects } = live.start({ players: this.#deps.players, levels: this.#deps.items.map((item) => item.level) }, this.#now());
    this.#commit(state, effects);
  }

  answer(player: string, slot: number, answer: unknown): AnswerResult {
    const item = this.#deps.items[slot];
    if (!item) return { ok: false, error: "malformed_answer" };
    const kind = kinds[item.kind];
    let stored: unknown;
    try {
      stored = kind.normalize(item.payload, this.#resolved[slot], answer);
    } catch (error) {
      if (error instanceof MalformedAnswer) return { ok: false, error: "malformed_answer" };
      throw error;
    }
    const credit = kind.grade(item.payload, this.#resolved[slot], stored);
    const accepted = this.#apply({ type: "answer", player, slot, credit, at: this.#now() }, new Map([[player, stored]]));
    return accepted ? { ok: true } : { ok: false, error: "not_accepted" };
  }

  leave(player: string): void {
    this.#apply({ type: "leave", player, at: this.#now() }, new Map());
  }

  #now(): number {
    return this.#deps.clock.now().getTime();
  }

  /** Applies one event; returns false when the format ignored it. */
  #apply(event: LiveEvent, values: Map<string, unknown>): boolean {
    const { state, effects } = live.next(this.#state, event);
    if (state === this.#state) return false;
    this.#commit(state, effects, values, credits(event));
    return true;
  }

  #commit(state: LiveState, effects: LiveEffect[], values = new Map<string, unknown>(), credit = 0): void {
    const { sessions, sessionId } = this.#deps;
    const at = new Date(this.#now()).toISOString();
    for (const effect of effects) {
      if (effect.type === "answered") {
        const value = values.get(effect.player) ?? null;
        sessions.recordAnswer({ sessionId, playerId: effect.player, slot: effect.slot, value, credit, points: effect.points, answeredAt: at });
        this.#round(effect.slot).set(effect.player, { value, points: effect.points });
      } else if (effect.type === "missed") {
        sessions.recordAnswer({ sessionId, playerId: effect.player, slot: effect.slot, value: null, credit: 0, points: 0, answeredAt: null });
        this.#round(effect.slot).set(effect.player, { value: null, points: 0 });
      } else if (effect.type === "ended") {
        sessions.finish({ sessionId, endedAt: at, reason: effect.ending.reason, result: effect.ending });
      }
    }
    this.#state = state;
    this.#cancelTimer?.();
    this.#cancelTimer = state.phase === "ended" ? null : this.#deps.schedule(state.dueAt, () => this.#apply({ type: "timeout", at: this.#now() }, new Map()));
    this.#deps.publish(this.#view());
  }

  #round(slot: number) {
    let round = this.#rounds.get(slot);
    if (!round) this.#rounds.set(slot, (round = new Map()));
    return round;
  }

  #view(): LiveView {
    const state = this.#state;
    const showsQuestion = state.phase === "question" || state.phase === "reveal";
    const item = showsQuestion ? this.#deps.items[state.slot] : undefined;
    const resolved = this.#resolved[state.slot];
    const round = this.#rounds.get(state.slot);
    return {
      version: ++this.#version,
      phase: state.phase,
      slot: state.slot,
      total: this.#deps.items.length,
      dueAt: state.dueAt,
      questionEndsAt: state.questionEndsAt,
      question: item ? { prompt: item.prompt, kind: item.kind, level: item.level, card: kinds[item.kind].card(item.payload, resolved) } : null,
      reveal: item && state.phase === "reveal"
        ? {
          answer: kinds[item.kind].reveal(item.payload, resolved),
          explanation: item.explanation,
          answers: state.seats.flatMap((seat) => {
            const given = round?.get(seat.player);
            return given ? [{ player: seat.player, value: given.value, points: given.points }] : [];
          }),
        }
        : null,
      players: state.seats.map((seat) => ({ player: seat.player, status: seat.status, score: seat.score, answered: state.phase === "question" && state.answered.includes(seat.player) })),
      standings: state.ended?.standings ?? null,
    };
  }
}

const credits = (event: LiveEvent) => (event.type === "answer" ? event.credit : 0);
