import type { Clock } from "../../server/core/clock";
import type { Schedule } from "../../server/core/schedule";
import type { SessionsStore } from "../../server/core/sessions.store";
import { poll, pollResults, type PollEvent, type PollState } from "./formats/poll";
import { optionsFor, type PollKind, type PollOption } from "./options";

const IDLE_MS = 48 * 3600_000;

export interface PollItem { prompt: string; kind: PollKind; payload: unknown }

interface Deps {
  sessionId: string;
  players: string[];
  items: PollItem[];
  clock: Clock;
  schedule: Schedule;
  sessions: SessionsStore;
  onChange: () => void;
}

/** One running poll. Answers stay private until the end; each closed question is recorded then. */
export class PollGame {
  readonly #deps: Deps;
  readonly #options: { options: PollOption[]; notApplicable: number | null }[];
  #state: PollState;
  #cancelIdle: () => void = () => {};

  constructor(deps: Deps) {
    this.#deps = deps;
    this.#options = deps.items.map((item) => optionsFor(item.kind, item.payload, deps.players));
    this.#state = poll.start({ players: deps.players, slots: this.#options.map((o) => ({ options: o.options.length, notApplicable: o.notApplicable })) }, this.#now());
    this.#scheduleIdle();
  }

  get ended() {
    return this.#state.ended;
  }

  answer(player: string, slot: number, option: number): boolean {
    return this.#apply({ type: "answer", player, slot, option, at: this.#now() });
  }

  leave(player: string): boolean {
    return this.#apply({ type: "leave", player, at: this.#now() });
  }

  view(viewer: string | null) {
    const state = this.#state;
    const current = state.current;
    const open = current === null ? null : state.slots[current]!;
    return {
      question: current === null ? null : {
        slot: current, total: this.#deps.items.length, prompt: this.#deps.items[current]!.prompt, options: this.#options[current]!.options, servedAt: open!.servedAt,
      },
      // Who answered the open question, never what.
      answered: open ? Object.keys(open.answers) : [],
      mine: open && viewer !== null ? open.answers[viewer] ?? null : null,
      inactive: state.inactive,
      ...(state.ended ? { answers: state.slots.map((slot) => slot.answers), results: pollResults(state) } : {}),
    };
  }

  #now() {
    return this.#deps.clock.now().getTime();
  }

  #apply(event: PollEvent): boolean {
    const before = this.#state;
    const { state } = poll.next(before, event);
    if (state === before) return false;
    this.#state = state;
    // A question closes when the next opens or the game ends; its final answers are recorded then.
    const closedUpTo = state.current ?? state.slots.length;
    const previouslyOpen = before.current ?? before.slots.length;
    for (let slot = previouslyOpen; slot < closedUpTo; slot++) this.#record(slot);
    if (state.ended && !before.ended) {
      if (state.ended.reason !== "resolved" && before.current !== null) this.#record(before.current);
      this.#cancelIdle();
      this.#deps.sessions.finish({ sessionId: this.#deps.sessionId, endedAt: new Date(this.#now()).toISOString(), reason: state.ended.reason, result: pollResults(state) });
    } else if (state.current !== before.current) {
      this.#scheduleIdle();
    }
    this.#deps.onChange();
    return true;
  }

  #record(slot: number) {
    for (const [player, option] of Object.entries(this.#state.slots[slot]?.answers ?? {})) {
      this.#deps.sessions.recordAnswer({ sessionId: this.#deps.sessionId, playerId: player, slot, value: option, credit: 0, points: 0, answeredAt: null });
    }
  }

  #scheduleIdle() {
    this.#cancelIdle();
    const current = this.#state.current;
    if (current === null) return;
    this.#cancelIdle = this.#deps.schedule(this.#state.slots[current]!.servedAt + IDLE_MS, () => this.#apply({ type: "settle", at: this.#now() }));
  }
}
