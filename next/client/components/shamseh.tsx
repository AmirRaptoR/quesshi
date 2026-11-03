/**
 * The shamseh, a twelve-ray Persian rosette, is the whole scoreboard: half the rays are yours and
 * half are theirs, one per question; saffron for right, dim pomegranate for wrong, dashed for a
 * question not yet reached. Past twenty questions a ray is thinner than its outline, so two rings
 * say the same thing at any length.
 */
type Run = readonly (boolean | null)[] | undefined;

interface Props {
  mine?: Run;
  theirs?: Run;
  hideTheirs?: boolean;
  ignite?: boolean;
  size?: string;
  centre?: string;
  label: string;
  /** Questions per player. */
  slots?: number;
}

const MAX_RAYS = 20;
const f = (value: number) => String(Math.round(value * 100) / 100);
const count = (run: Run, right: boolean) => (run ?? []).filter((answer) => answer === right).length;
const stateOf = (run: Run, slot: number) => {
  const answer = run?.[slot];
  return answer === undefined || answer === null ? "idle" : answer ? "right" : "wrong";
};
const point = (radius: number, degrees: number) => {
  const radians = (degrees * Math.PI) / 180;
  return `${f(50 + radius * Math.cos(radians))},${f(50 + radius * Math.sin(radians))}`;
};
// Ray 0 points up; the first half sweeps clockwise down the right side, the rest back up the left.
const ray = (index: number, total: number) => {
  const step = 360 / total;
  const angle = index * step - 90;
  return [point(46, angle), point(27, angle - step / 2), point(15, angle), point(27, angle + step / 2)].join(" ");
};

export function Shamseh({ mine, theirs, hideTheirs = false, ignite = false, size = "100%", centre, label, slots = 6 }: Props) {
  return (
    <svg class={`shamseh${ignite ? " shamseh--ignite" : ""}`} viewBox="0 0 100 100" width={size} height={size} role="img" aria-label={label}>
      {slots > MAX_RAYS ? (
        <g class="shamseh__rings">
          {[{ run: mine, r: 42, hide: false }, { run: theirs, r: 30, hide: hideTheirs }].map((ring) => {
            const circumference = 2 * Math.PI * ring.r;
            const right = ring.hide ? 0 : count(ring.run, true);
            const wrong = ring.hide ? 0 : count(ring.run, false);
            return (
              <>
                <circle class="shamseh__track" cx="50" cy="50" r={f(ring.r)} />
                <circle class="shamseh__arc shamseh__arc--right" cx="50" cy="50" r={f(ring.r)} stroke-dasharray={`${f((circumference * right) / slots)} ${f(circumference)}`} />
                <circle class="shamseh__arc shamseh__arc--wrong" cx="50" cy="50" r={f(ring.r)}
                  stroke-dasharray={`${f((circumference * wrong) / slots)} ${f(circumference)}`} stroke-dashoffset={f((-circumference * right) / slots)} />
              </>
            );
          })}
        </g>
      ) : (
        <g>
          {Array.from({ length: slots * 2 }, (_, i) => {
            const isMine = i < slots;
            const slot = isMine ? i : i - slots;
            const state = isMine ? stateOf(mine, slot) : hideTheirs ? "hidden" : stateOf(theirs, slot);
            return <polygon class={`shamseh__ray shamseh__ray--${state}`} points={ray(i, slots * 2)} style={ignite ? `animation-delay:${i * 55}ms` : undefined} />;
          })}
        </g>
      )}
      <line class="shamseh__seam" x1="50" y1="2" x2="50" y2="98" />
      <circle class="shamseh__hub" cx="50" cy="50" r="13" />
      {centre && (
        <text x="50" y="50" text-anchor="middle" dominant-baseline="central" fill="var(--sadaf)" font-size="11" font-weight="800" font-family="Vazirmatn, sans-serif">{centre}</text>
      )}
    </svg>
  );
}
