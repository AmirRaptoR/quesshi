import { useApp } from "../../../../client/app";
import { initial, tint } from "../logic/avatar";

export interface DuelBarSide { id: string; name: string; avatar: string; score: number; out: boolean }

/** Every score visible through the question and reveal; one column for a two-player game, a strip beyond that. */
export function DuelBar({ mine, others }: { mine: DuelBarSide; others: DuelBarSide[] }) {
  const { t } = useApp();
  const side = (s: DuelBarSide) => (
    <div class={`duelbar__side duelbar__side--theirs${others.length > 1 && s.out ? " duelbar__side--out" : ""}`} key={s.id}>
      <span class="numeral duelbar__score">{t.num(s.score)}</span>
      <span class="duelbar__name" dir="auto">{s.name}</span>
      <span class="avatar" style={`background:${tint(s.avatar)}`}>{initial(s.name)}</span>
    </div>
  );
  return (
    <div class="duelbar">
      <div class="duelbar__side">
        <span class="avatar" style={`background:${tint(mine.avatar)}`}>{initial(mine.name)}</span>
        <span class="duelbar__name" dir="auto">{mine.name}</span>
        <span class="numeral duelbar__score duelbar__score--mine">{t.num(mine.score)}</span>
      </div>
      {others.length === 1 && side(others[0]!)}
      {others.length > 1 && <div class="duelbar__others">{others.map(side)}</div>}
    </div>
  );
}
