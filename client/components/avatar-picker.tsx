import { AVATAR_SEEDS, tint } from "../avatar";

/** Today's twelve swatches; the chosen one is ringed. */
export function AvatarPicker({ value, onPick }: { value: string; onPick(seed: string): void }) {
  return (
    <div class="picker">
      {AVATAR_SEEDS.map((seed) => (
        <button type="button" class={`picker__chip${value === seed ? " is-on" : ""}`} style={`--pick:${tint(seed)}`} aria-pressed={value === seed} onClick={() => onPick(seed)}>
          <span class="avatar avatar--sm" style={`background:${tint(seed)}`} />
        </button>
      ))}
    </div>
  );
}
