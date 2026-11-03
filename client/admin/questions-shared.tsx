import { useEffect, useState } from "preact/hooks";
import { get } from "../api";
import type { Media } from "../components/media-block";
import { clientFeatures } from "../features";
import type { Translator } from "../i18n";

export interface Category { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; color: string; isActive: boolean; sortOrder: number }

export interface AdminQuestion {
  id: string; kind: string; lang: string; categoryId: string | null; level: number | null; prompt: string; payload: unknown;
  explanation: string | null; media?: Media | null; status: string; createdAt: string;
}

export interface QuestionKind { kind: string; feature: string; levelled: boolean }

export const LEVELS = [1, 2, 3, 4, 5];
export const LANG_CODES = ["fa", "en", "nl"];

export function useCategories(): Category[] {
  const [categories, setCategories] = useState<Category[]>([]);
  useEffect(() => {
    get<Category[]>("/api/admin/categories").then(setCategories).catch(() => {});
  }, []);
  return categories;
}

/** A category's display name, falling back to the id for one that has since been deleted. */
export function categoryName(t: Translator, categories: readonly Category[], id: string | null): string {
  if (!id) return t.t("admin.uncategorized");
  const category = categories.find((c) => c.id === id);
  return category ? t.name(category) : id;
}

/** How the enabled features show a payload of this kind; nothing when no feature draws it. */
export function KindPreview({ kind, payload }: { kind: string; payload: unknown }) {
  const Preview = clientFeatures.find((feature) => feature.questionPreviews?.[kind])?.questionPreviews?.[kind];
  return Preview ? <Preview payload={payload} /> : null;
}

/** The enabled features' editor for this kind's payload, if one exists. */
export const kindForm = (kind: string) => clientFeatures.find((feature) => feature.questionForms?.[kind])?.questionForms?.[kind];
