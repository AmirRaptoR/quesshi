const IDLE_MS = 48 * 3600_000;

/** How a question's served options look: how many, and which one (if any) is "not applicable". */
export interface SlotShape { options: number; notApplicable: number | null }
export interface PollSlot { servedAt: number; answers: Record<string, number> }
export interface PollState {
  players: string[];
  inactive: string[];
  shapes: SlotShape[];
  slots: PollSlot[];
  current: number | null;
  ended: { reason: "resolved" | "no_contest" } | null;
}

export type PollEvent =
  | { type: "answer"; player: string; slot: number; option: number; at: number }
  | { type: "leave"; player: string; at: number }
  | { type: "settle"; at: number };

/**
 * Today's voting: everyone answers each question, and may change their answer until every active
 * player has answered; then the next opens. Players idle for 48 hours on a question become inactive.
 */
export const poll = {
  id: "poll",
  start({ players, slots }: { players: string[]; slots: SlotShape[] }, at: number): PollState {
    return { players, inactive: [], shapes: slots, slots: [{ servedAt: at, answers: {} }], current: 0, ended: null };
  },

  next(state: PollState, event: PollEvent): { state: PollState } {
    const settled = settle(state, event.at);
    if (settled.ended || settled.current === null) return { state: settled };
    if (event.type === "settle") return { state: settled };
    if (event.type === "leave") {
      if (!active(settled).includes(event.player)) return { state: settled };
      return { state: afterRosterChange({ ...settled, inactive: [...settled.inactive, event.player] }, event.at) };
    }
    const shape = settled.shapes[settled.current]!;
    const valid = event.slot === settled.current && active(settled).includes(event.player) && Number.isInteger(event.option) && event.option >= 0 && event.option < shape.options;
    if (!valid) return { state: settled };
    const slots = settled.slots.map((slot, index) => (index === settled.current ? { ...slot, answers: { ...slot.answers, [event.player]: event.option } } : slot));
    return { state: advance({ ...settled, slots }, event.at) };
  },
};

const active = (state: PollState) => state.players.filter((player) => !state.inactive.includes(player));

/** Marks players idle on the open question inactive. */
function settle(state: PollState, at: number): PollState {
  if (state.ended || state.current === null) return state;
  const slot = state.slots[state.current]!;
  if (at - slot.servedAt < IDLE_MS) return state;
  const idle = active(state).filter((player) => !(player in slot.answers));
  return idle.length ? afterRosterChange({ ...state, inactive: [...state.inactive, ...idle] }, at) : state;
}

function afterRosterChange(state: PollState, at: number): PollState {
  return active(state).length < 2 ? { ...state, current: null, ended: { reason: "no_contest" } } : advance(state, at);
}

/** Opens the next question, or ends the game, once every active player has answered the open one. */
function advance(state: PollState, at: number): PollState {
  const slot = state.slots[state.current!]!;
  if (!active(state).every((player) => player in slot.answers)) return state;
  const next = state.current! + 1;
  if (next >= state.shapes.length) return { ...state, current: null, ended: { reason: "resolved" } };
  return { ...state, current: next, slots: [...state.slots, { servedAt: at, answers: {} }] };
}

export interface PollResults {
  /** Per question: option counts and whether all comparable answers agreed; null while it is open. */
  slots: ({ counts: number[]; allAgreed: boolean } | null)[];
  /** Agreement between each pair of players, only for a resolved game. */
  pairs: { players: [string, string]; same: number; different: number; percent: number | null }[] | null;
  allAgreedCount: number | null;
}

/** Today's voting results: "not applicable" answers are counted but never compared. */
export function pollResults(state: PollState): PollResults {
  const closed = state.slots.map((_, index) => state.current === null ? (state.ended?.reason === "resolved" || index < state.slots.length - 1 || allAnswered(state, index)) : index < state.current);
  const comparable = (index: number, player: string) => {
    const option = state.slots[index]!.answers[player];
    return option === undefined || option === state.shapes[index]!.notApplicable ? null : option;
  };
  const slots = state.slots.map((slot, index) => {
    if (!closed[index]) return null;
    const counts = Array<number>(state.shapes[index]!.options).fill(0);
    for (const option of Object.values(slot.answers)) counts[option]!++;
    const choices = state.players.map((player) => comparable(index, player)).filter((option): option is number => option !== null);
    return { counts, allAgreed: choices.length >= 2 && choices.every((option) => option === choices[0]) };
  });
  if (state.ended?.reason !== "resolved") return { slots, pairs: null, allAgreedCount: null };
  const pairs: NonNullable<PollResults["pairs"]> = [];
  state.players.forEach((first, i) => state.players.slice(i + 1).forEach((second) => {
    let same = 0, different = 0;
    state.slots.forEach((_, index) => {
      const left = comparable(index, first), right = comparable(index, second);
      if (left === null || right === null) return;
      if (left === right) same++;
      else different++;
    });
    const total = same + different;
    pairs.push({ players: [first, second], same, different, percent: total === 0 ? null : Math.floor((100 * same) / total + 0.5) });
  }));
  return { slots, pairs, allAgreedCount: slots.filter((slot) => slot?.allAgreed).length };
}

const allAnswered = (state: PollState, index: number) => state.players.every((player) => player in state.slots[index]!.answers);
