import { useApp } from "../../../../client/app";
import { sortRows, type SortRow } from "../logic/answer-reveal";

export interface SortColumn { label: string; placed: string[] | null; answered: boolean }

const tone = (row: SortRow) => (row.placed === null ? "sortreveal__item--blank" : row.right ? "sortreveal__item--right" : "sortreveal__item--wrong");

/** The right order, and each player's beside it: two columns make "these disagree at place three" visible. */
export function SortReveal({ correct, columns }: { correct: string[]; columns: SortColumn[] }) {
  const { t } = useApp();
  return (
    <div class="sortreveal">
      <div class="sortreveal__col">
        <div class="sortreveal__head">{t.t("sort.correctOrder")}</div>
        {correct.map((text, place) => (
          <div class="sortreveal__item sortreveal__item--right" key={place}>
            <span class="sortreveal__place">{t.num(place + 1)}</span>
            <span class="grow" dir="auto">{text}</span>
          </div>
        ))}
      </div>
      {columns.map((column) => (
        <div class="sortreveal__col" key={column.label}>
          <div class="sortreveal__head" dir="auto">{column.label}</div>
          {!column.answered ? (
            <div class="sortreveal__item sortreveal__item--blank"><span class="grow" dir="auto">{t.t("live.noAnswer")}</span></div>
          ) : (
            sortRows(correct, column.placed).map((row) => (
              <div class={`sortreveal__item ${tone(row)}`} key={row.place}>
                <span class="sortreveal__place">{t.num(row.place + 1)}</span>
                <span class="grow" dir="auto">{row.placed ?? "—"}</span>
              </div>
            ))
          )}
        </div>
      ))}
    </div>
  );
}
