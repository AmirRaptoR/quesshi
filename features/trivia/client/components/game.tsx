import type { ComponentChildren } from "preact";
import { useEffect, useState } from "preact/hooks";
import { useApp } from "../../../../client/app";
import { get } from "../../../../client/api";
import type { Category, Question } from "../types";

export const KEYS = ["A", "B", "C", "D"];

let loading: Promise<Category[]> | null = null;
/** Trivia's categories, fetched once, so a card can show its category's chip. */
export function useCategories(): Category[] {
  const [categories, setCategories] = useState<Category[]>([]);
  useEffect(() => {
    loading ??= get<Category[]>("/f/trivia/categories").catch(() => {
      loading = null;
      return [];
    });
    void loading.then(setCategories);
  }, []);
  return categories;
}

/** The bar above a question: its category and level, where the game is, and the clock. */
export function GameHud({ question, slot, total, clock }: { question: Question; slot: number; total: number; clock: ComponentChildren }) {
  const { t } = useApp();
  const category = useCategories().find((c) => c.id === question.categoryId);
  const level = question.level ?? 3;
  const progress = total === 0 ? 0 : Math.floor((100 * (slot + 1)) / total);
  return (
    <header class="topbar game-hud">
      {category && (
        <span class="chip" style={`border-color:${category.color}66`}>
          <span aria-hidden="true">{category.icon}</span> {t.name(category)}
        </span>
      )}
      <span class="level" aria-label={t.t(`level.${level}`)}>
        {[1, 2, 3, 4, 5].map((i) => <i class={i <= level ? "on" : undefined} />)}
      </span>
      <div class="topbar__spacer" />
      <span class="muted numeral">{t.num(slot + 1)} / {t.num(total)}</span>
      {clock}
      <span class="game-progress" style={`--game-progress:${progress}%`} aria-hidden="true"><i /></span>
    </header>
  );
}
