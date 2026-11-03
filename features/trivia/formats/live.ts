import { NETWORK_GRACE_MS, score } from "../scoring";

const COUNTDOWN_MS = 3_000;
const QUESTION_MS = 20_000;
const REVEAL_MS = 3_000;
const MISSES_BEFORE_ELIMINATION = 3;

export type SeatStatus = "active" | "left" | "eliminated" | "done";
export interface LiveSeat { player: string; status: SeatStatus; score: number; exitRound: number | null }
export interface Standing { player: string; score: number; place: number; outcome: "win" | "draw" | "loss" | "none" }
export interface LiveEnding { reason: "completed" | "no_contest" | "ended_by_admin"; standings: Standing[] }

export interface LiveState {
  phase: "countdown" | "question" | "reveal" | "ended";
  slot: number;
  /** When the current phase ends (for a question: its time limit plus the network grace). */
  dueAt: number;
  questionEndsAt: number | null;
  levels: (number | null)[];
  seats: LiveSeat[];
  /** Players expected to answer the open question, fixed when it opens. */
  expected: string[];
  answered: string[];
  missStreaks: Record<string, number>;
  ended: LiveEnding | null;
}

export type LiveEvent =
  | { type: "timeout"; at: number }
  | { type: "answer"; player: string; slot: number; credit: number; at: number }
  | { type: "leave"; player: string; at: number };

export type LiveEffect =
  | { type: "answered"; player: string; slot: number; points: number }
  | { type: "missed"; player: string; slot: number }
  | { type: "eliminated"; player: string }
  | { type: "released"; slot: number }
  | { type: "ended"; ending: LiveEnding };

interface Step { state: LiveState; effects: LiveEffect[] }

/**
 * Today's live duel: everyone sees the same question at the same time. A 3 s countdown, then for each
 * question 20 s to answer and a 3 s reveal. Pure: time arrives with each event.
 */
export const live = {
  id: "live",
  start({ players, levels }: { players: string[]; levels: (number | null)[] }, now: number): Step {
    return {
      state: {
        phase: "countdown", slot: 0, dueAt: now + COUNTDOWN_MS, questionEndsAt: null, levels,
        seats: players.map((player) => ({ player, status: "active", score: 0, exitRound: null })),
        expected: [], answered: [], missStreaks: {}, ended: null,
      },
      effects: [],
    };
  },

  /** An administrator ends the game: a no contest, whatever the scores. */
  abort(state: LiveState): Step {
    return state.phase === "ended" ? unchanged(state) : end(state, "ended_by_admin", []);
  },

  next(state: LiveState, event: LiveEvent): Step {
    if (state.phase === "ended") return unchanged(state);
    switch (event.type) {
      case "timeout": return timeout(state);
      case "answer": return answer(state, event);
      case "leave": return leave(state, event);
    }
  },
};

const unchanged = (state: LiveState): Step => ({ state, effects: [] });
const active = (state: LiveState) => state.seats.filter((seat) => seat.status === "active");

function timeout(state: LiveState): Step {
  if (state.phase === "countdown") return openQuestion(state, state.dueAt, 0, []);
  if (state.phase === "question") return closeRound(state, []);
  // The reveal is over: the next question opens, or the session ends after the last one.
  const slot = state.slot + 1;
  return slot >= state.levels.length ? end(state, "completed", []) : openQuestion(state, state.dueAt, slot, []);
}

function openQuestion(state: LiveState, startsAt: number, slot: number, effects: LiveEffect[]): Step {
  const expected = active(state).map((seat) => seat.player);
  if (expected.length === 0) return end(state, "no_contest", effects);
  if (expected.length === 1) return end(state, "completed", effects);
  const questionEndsAt = startsAt + QUESTION_MS;
  return { state: { ...state, phase: "question", slot, questionEndsAt, dueAt: questionEndsAt + NETWORK_GRACE_MS, expected, answered: [] }, effects };
}

function answer(state: LiveState, event: Extract<LiveEvent, { type: "answer" }>): Step {
  const seat = state.seats.find((candidate) => candidate.player === event.player);
  const counts = state.phase === "question"
    && event.slot === state.slot
    && event.at <= state.dueAt
    && seat?.status === "active"
    && state.expected.includes(event.player)
    && !state.answered.includes(event.player);
  if (!counts) return unchanged(state);

  const points = score({ credit: event.credit, takenMs: event.at - (state.questionEndsAt! - QUESTION_MS), limitMs: QUESTION_MS, level: state.levels[state.slot] ?? null });
  const next: LiveState = {
    ...state,
    seats: state.seats.map((candidate) => (candidate.player === event.player ? { ...candidate, score: candidate.score + points } : candidate)),
    answered: [...state.answered, event.player],
    missStreaks: { ...state.missStreaks, [event.player]: 0 },
  };
  const effects: LiveEffect[] = [{ type: "answered", player: event.player, slot: state.slot, points }];
  return everyoneAnswered(next) ? closeRound(next, effects) : { state: next, effects };
}

const everyoneAnswered = (state: LiveState) => state.expected.every((player) => state.answered.includes(player));

function closeRound(state: LiveState, effects: LiveEffect[]): Step {
  const missStreaks = { ...state.missStreaks };
  let seats = state.seats;
  const out = [...effects];
  for (const player of state.expected.filter((candidate) => !state.answered.includes(candidate))) {
    out.push({ type: "missed", player, slot: state.slot });
    missStreaks[player] = (missStreaks[player] ?? 0) + 1;
    if (missStreaks[player] === MISSES_BEFORE_ELIMINATION) {
      seats = seats.map((seat) => (seat.player === player ? { ...seat, status: "eliminated", exitRound: state.slot } : seat));
      out.push({ type: "eliminated", player });
    }
  }
  out.push({ type: "released", slot: state.slot });
  const closed: LiveState = { ...state, seats, missStreaks };
  const remaining = active(closed).length;
  if (remaining === 0) return end(closed, "no_contest", out);
  if (remaining === 1) return end(closed, "completed", out);
  // The reveal ends on the question's schedule, however early the round closed.
  return { state: { ...closed, phase: "reveal", dueAt: state.questionEndsAt! + REVEAL_MS, questionEndsAt: null, expected: [], answered: [] }, effects: out };
}

function leave(state: LiveState, event: Extract<LiveEvent, { type: "leave" }>): Step {
  const seat = state.seats.find((candidate) => candidate.player === event.player);
  if (seat?.status !== "active") return unchanged(state);
  let next: LiveState = { ...state, seats: state.seats.map((candidate) => (candidate === seat ? { ...seat, status: "left", exitRound: state.slot } : candidate)) };
  // A player who already answered stays counted; one who had not is no longer waited for.
  if (state.phase === "question" && !state.answered.includes(event.player)) {
    next = { ...next, expected: next.expected.filter((player) => player !== event.player) };
  }
  const remaining = active(next).length;
  if (remaining === 0) return end(next, "no_contest", []);
  if (remaining === 1) return end(next, "completed", []);
  if (next.phase === "question" && everyoneAnswered(next)) return closeRound(next, []);
  return { state: next, effects: [] };
}

function end(state: LiveState, reason: LiveEnding["reason"], effects: LiveEffect[]): Step {
  const ending: LiveEnding = { reason, standings: reason === "completed" ? standings(state) : noContest(state) };
  const seats = state.seats.map((seat) => (seat.status === "active" ? { ...seat, status: "done" as const } : seat));
  return { state: { ...state, phase: "ended", seats, ended: ending }, effects: [...effects, { type: "ended", ending }] };
}

const noContest = (state: LiveState): Standing[] => state.seats.map((seat) => ({ player: seat.player, score: 0, place: 0, outcome: "none" }));

/** Finishers by score above abandoners by how late they went out; equal entries share a place. */
function standings(state: LiveState): Standing[] {
  const ranked = state.seats
    .map((seat) => ({ seat, abandoned: seat.status === "left" || seat.status === "eliminated" }))
    .map((entry) => ({ ...entry, key: entry.abandoned ? entry.seat.exitRound ?? -1 : entry.seat.score }))
    .sort((a, b) => Number(a.abandoned) - Number(b.abandoned) || b.key - a.key || (a.seat.player < b.seat.player ? -1 : 1));
  const places: number[] = [];
  ranked.forEach((entry, i) => {
    const previous = ranked[i - 1];
    places.push(previous && previous.abandoned === entry.abandoned && previous.key === entry.key ? places[i - 1]! : i + 1);
  });
  const sharedFirst = places.filter((place) => place === 1).length > 1;
  return ranked.map((entry, i) => ({
    player: entry.seat.player,
    score: entry.abandoned ? 0 : entry.seat.score,
    place: places[i]!,
    outcome: places[i] === 1 ? (sharedFirst ? "draw" : "win") : "loss",
  }));
}
