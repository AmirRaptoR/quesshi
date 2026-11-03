import { useApp } from "../../../../client/app";
import { initial, tint } from "../logic/avatar";
import { chipClass, outcomeKey } from "../logic/outcome";
import type { Participant, Standing } from "../types";

/** Every participant with place, score and outcome, already ranked by the server. */
export function StandingsList({ rows, people, me }: { rows: Standing[]; people: readonly Participant[]; me: string }) {
  const { t } = useApp();
  return (
    <div class="stack" style="gap:0">
      {rows.map((row) => {
        const person = people.find((p) => p.id === row.player);
        const name = person?.displayName ?? "";
        return (
          <div class={`stand-row${row.place === 1 ? " stand-row--first" : ""}${row.player === me ? " stand-row--me" : ""}`} key={row.player}>
            <span class="stand-row__place numeral">{t.num(row.place)}</span>
            <span class="avatar avatar--sm" style={`background:${tint(person?.avatarSeed ?? row.player)}`}>{initial(name)}</span>
            <span class="grow" style="min-width:0">
              <div class="stand-row__name" dir="auto">{name}</div>
            </span>
            <span class="numeral stand-row__score">{t.num(row.score)}</span>
            <span class={`chip ${chipClass(row.outcome)}`}>{t.t(outcomeKey(row.outcome))}</span>
          </div>
        );
      })}
    </div>
  );
}
