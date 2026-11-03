import type { Clock } from "./clock";

/** Runs a callback at a point in time and returns a function that cancels it. Tests use a fake one. */
export type Schedule = (at: number, run: () => void) => () => void;

export function realSchedule(clock: Clock): Schedule {
  return (at, run) => {
    const timer = setTimeout(run, Math.max(0, at - clock.now().getTime()));
    return () => clearTimeout(timer);
  };
}
