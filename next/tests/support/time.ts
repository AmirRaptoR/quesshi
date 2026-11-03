import type { Clock } from "../../server/core/clock";
import type { Schedule } from "../../server/core/schedule";

/** A clock and scheduler that only move when a test advances them. Due callbacks run in time order. */
export function fakeScheduler(start: number) {
  let now = start;
  let jobs: { at: number; run: () => void; id: number }[] = [];
  let ids = 0;
  const clock: Clock = { now: () => new Date(now) };
  const schedule: Schedule = (at, run) => {
    const id = ++ids;
    jobs.push({ at, run, id });
    return () => { jobs = jobs.filter((job) => job.id !== id); };
  };
  return {
    clock,
    schedule,
    advanceTo(target: number) {
      for (;;) {
        const due = jobs.filter((job) => job.at <= target).sort((a, b) => a.at - b.at || a.id - b.id)[0];
        if (!due) break;
        jobs = jobs.filter((job) => job !== due);
        now = Math.max(now, due.at);
        due.run();
      }
      now = Math.max(now, target);
    },
    pending: () => jobs.length,
  };
}

/** A random source that always returns the same value, for deterministic shuffles. */
export const fixedRandom = () => 0.42;

/** A repeatable sequence of different numbers (mulberry32), for code that must not draw the same value twice. */
export function seededRandom(seed = 1): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
