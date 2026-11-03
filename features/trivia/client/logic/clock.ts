/** Whole seconds left until a deadline, a part second counting as one. */
export const remainingSeconds = (endsAt: number, now: number) => Math.max(0, Math.ceil((endsAt - now) / 1000));

/** The tick cue sounds once for each of the last three seconds. */
export const shouldTick = (remaining: number, lastTicked: number | null) => remaining >= 1 && remaining <= 3 && remaining !== lastTicked;
