import { useRef, useState } from "preact/hooks";
import { useApp } from "../../../../client/app";
import { move, moveDown, moveUp, targetPlace, type Ordering } from "../logic/sort-ordering";

/**
 * A sorting question's answer: the served items, dragged into order by their grips. Pointer events
 * rather than HTML drag and drop, which does not fire on touch in Safari. Only the grip starts a
 * drag, so a scroll that begins on a row still scrolls; the arrow keys move a row too.
 */
export function SortList({ items, ordering, onChange, disabled }: { items: string[]; ordering: Ordering; onChange(next: Ordering): void; disabled: boolean }) {
  const { t } = useApp();
  const list = useRef<HTMLOListElement>(null);
  const [dragging, setDragging] = useState<number | null>(null);
  const drag = useRef<{ pointer: number; midpoints: number[] } | null>(null);

  const start = (place: number, event: PointerEvent) => {
    if (disabled || !list.current) return;
    // Measured once: four short rows are near enough the same height.
    const midpoints = [...list.current.children].map((row) => {
      const box = row.getBoundingClientRect();
      return box.top + box.height / 2;
    });
    drag.current = { pointer: event.pointerId, midpoints };
    setDragging(place);
    // Captured on the list, so a finger sliding off the narrow grip keeps dragging.
    try {
      list.current.setPointerCapture(event.pointerId);
    } catch {
      // The pointer is already gone.
    }
  };
  const moveTo = (event: PointerEvent) => {
    if (dragging === null || !drag.current || event.pointerId !== drag.current.pointer) return;
    const to = targetPlace(drag.current.midpoints, event.clientY);
    if (to < 0 || to === dragging) return;
    setDragging(to);
    onChange(move(ordering, dragging, to));
  };
  const end = (event: PointerEvent) => {
    if (dragging === null) return;
    setDragging(null);
    drag.current = null;
    try {
      list.current?.releasePointerCapture(event.pointerId);
    } catch {
      // Likewise.
    }
  };
  const key = (place: number, event: KeyboardEvent) => {
    if (disabled || (event.key !== "ArrowUp" && event.key !== "ArrowDown")) return;
    event.preventDefault();
    onChange(event.key === "ArrowUp" ? moveUp(ordering, place) : moveDown(ordering, place));
  };

  return (
    <>
      <ol class="sortlist" ref={list} onPointerMove={moveTo} onPointerUp={end} onPointerCancel={end}>
        {ordering.map((served, place) => (
          <li class={`sortrow${dragging === place ? " sortrow--dragging" : ""}`} key={served}>
            <button type="button" class="sortrow__grip" data-grip disabled={disabled} aria-label={t.format("sort.move", items[served] ?? "")}
              onPointerDown={(e) => start(place, e)} onKeyDown={(e) => key(place, e)}>
              <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true" focusable="false" fill="currentColor">
                <circle cx="4" cy="3" r="1" /><circle cx="8" cy="3" r="1" />
                <circle cx="4" cy="6" r="1" /><circle cx="8" cy="6" r="1" />
                <circle cx="4" cy="9" r="1" /><circle cx="8" cy="9" r="1" />
              </svg>
            </button>
            <span class="grow" dir="auto">{items[served]}</span>
            <span class="sortrow__place numeral">{t.num(place + 1)}</span>
          </li>
        ))}
      </ol>
      {!disabled && <p class="faint" style="margin:.55rem 0 0;font-size:var(--step--1)">{t.t("sort.hint")}</p>}
    </>
  );
}
