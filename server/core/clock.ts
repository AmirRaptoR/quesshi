/** The only source of time. Tests pass a fake one and advance it instead of waiting. */
export interface Clock {
  now(): Date;
}

export const systemClock: Clock = { now: () => new Date() };
