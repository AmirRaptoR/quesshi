import { useEffect, useState } from "preact/hooks";
import { remainingSeconds } from "../logic/clock";

const CIRCUMFERENCE = 2 * Math.PI * 19;

/** Countdown ring. The clock that counts is the server's; this one just shows it draining. */
export function Ring({ endsAt, remaining, total, size = "44" }: { endsAt?: number; remaining?: number; total: number; size?: string }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (endsAt === undefined) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 200);
    return () => clearInterval(timer);
  }, [endsAt]);
  const left = endsAt !== undefined ? remainingSeconds(endsAt, now) : (remaining ?? 0);
  const offset = CIRCUMFERENCE * (1 - Math.min(Math.max(total === 0 ? 0 : left / total, 0), 1));
  const heat = left <= 5 ? "hot" : left <= 10 ? "warn" : "";
  return (
    <svg class="ring" viewBox="0 0 44 44" width={size} height={size} aria-hidden="true">
      <circle class="ring__track" cx="22" cy="22" r="19" stroke-width="3" />
      <circle class={`ring__fill ${heat}`} cx="22" cy="22" r="19" stroke-width="3" stroke-dasharray={String(CIRCUMFERENCE)} stroke-dashoffset={String(offset)} />
    </svg>
  );
}
