import { NETWORK_GRACE_MS, score } from "../scoring";

const QUESTION_MS = 20_000;
const FORFEIT_MS = 48 * 3600_000;

export interface DuelAnswer { slot: number; points: number; missed: boolean }
export interface DuelRun { answers: DuelAnswer[]; servedAt: number | null }
export interface DuelStanding { player: string; score: number; place: number; outcome: "win" | "draw" | "loss" }
export interface DuelState {
  players: string[];
  levels: (number | null)[];
  createdAt: number;
  runs: Record<string, DuelRun>;
  ended: { reason: "resolved" | "forfeited"; standings: DuelStanding[] } | null;
}

export type DuelEvent =
  | { type: "serve"; player: string; at: number }
  | { type: "answer"; player: string; slot: number; credit: number; at: number };

export type DuelResult =
  | { type: "served"; slot: number; servedAt: number }
  | { type: "answered"; slot: number; points: number }
  | { type: "refused" };

/**
 * Today's asynchronous duel: each player goes through the same questions at their own pace, with a
 * 20 s clock per question. It ends when everyone has finished, or is forfeited 48 h after creation.
 */
export const asyncDuel = {
  id: "async_duel",
  start({ players, levels, createdAt }: { players: string[]; levels: (number | null)[]; createdAt: number }): DuelState {
    return { players, levels, createdAt, runs: Object.fromEntries(players.map((player) => [player, { answers: [], servedAt: null }])), ended: null };
  },

  next(state: DuelState, event: DuelEvent): { state: DuelState; result: DuelResult } {
    if (!state.ended && event.at >= state.createdAt + FORFEIT_MS) state = finish(state, "forfeited");
    const run = state.runs[event.player];
    if (state.ended || !run) return { state, result: { type: "refused" } };
    return event.type === "serve" ? serve(state, event.player, run, event.at) : answer(state, event, run);
  },
};

const finished = (state: DuelState, run: DuelRun) => run.answers.length >= state.levels.length;
const late = (servedAt: number, at: number) => at > servedAt + QUESTION_MS + NETWORK_GRACE_MS;

function serve(state: DuelState, player: string, run: DuelRun, at: number): { state: DuelState; result: DuelResult } {
  // An open question is returned as it is: asking again does not restart its clock.
  if (run.servedAt !== null && !late(run.servedAt, at)) return { state, result: { type: "served", slot: run.answers.length, servedAt: run.servedAt } };
  let current = run;
  if (run.servedAt !== null) {
    // The open question ran out: it is recorded as a miss and the run moves on.
    current = { answers: [...run.answers, { slot: run.answers.length, points: 0, missed: true }], servedAt: null };
    state = withRun(state, player, current);
    if (finished(state, current)) return { state: resolveIfDone(state), result: { type: "refused" } };
  }
  if (finished(state, current)) return { state, result: { type: "refused" } };
  return { state: withRun(state, player, { ...current, servedAt: at }), result: { type: "served", slot: current.answers.length, servedAt: at } };
}

function answer(state: DuelState, event: Extract<DuelEvent, { type: "answer" }>, run: DuelRun): { state: DuelState; result: DuelResult } {
  if (run.servedAt === null || event.slot !== run.answers.length) return { state, result: { type: "refused" } };
  const points = score({ credit: event.credit, takenMs: event.at - run.servedAt, limitMs: QUESTION_MS, level: state.levels[event.slot] ?? null });
  const next = withRun(state, event.player, { answers: [...run.answers, { slot: event.slot, points, missed: false }], servedAt: null });
  return { state: resolveIfDone(next), result: { type: "answered", slot: event.slot, points } };
}

const withRun = (state: DuelState, player: string, run: DuelRun): DuelState => ({ ...state, runs: { ...state.runs, [player]: run } });

function resolveIfDone(state: DuelState): DuelState {
  return state.players.every((player) => finished(state, state.runs[player]!)) ? finish(state, "resolved") : state;
}

/** Standings by score; equal scores share a place and the next is skipped; a shared first place is a draw. */
function finish(state: DuelState, reason: "resolved" | "forfeited"): DuelState {
  const totals = state.players.map((player) => ({ player, score: state.runs[player]!.answers.reduce((sum, a) => sum + a.points, 0) }));
  const ranked = [...totals].sort((a, b) => b.score - a.score);
  const places: number[] = [];
  ranked.forEach((entry, i) => places.push(i > 0 && entry.score === ranked[i - 1]!.score ? places[i - 1]! : i + 1));
  const sharedFirst = places.filter((place) => place === 1).length > 1;
  const standings = ranked.map((entry, i) => ({ ...entry, place: places[i]!, outcome: (places[i] === 1 ? (sharedFirst ? "draw" : "win") : "loss") as DuelStanding["outcome"] }));
  return { ...state, ended: { reason, standings } };
}
