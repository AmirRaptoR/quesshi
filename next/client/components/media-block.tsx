import { useApp } from "../app";

export interface Media { kind: "image" | "audio" | "video"; url: string; attribution: string | null }

/** The provider packs the source link onto the end of the credit; it is shown as a link. */
export function splitCredit(text: string): { credit: string; source: string | null } {
  const marker = text.indexOf(" — http");
  return marker < 0 ? { credit: text, source: null } : { credit: text.slice(0, marker), source: text.slice(marker + 3).trim() };
}

/** A question's image, audio or video, presented the same way on every screen. */
export function MediaBlock({ media, compact = false, autoplay = true, loop = true }: { media: Media | null; compact?: boolean; autoplay?: boolean; loop?: boolean }) {
  const { t } = useApp();
  if (!media) return null;
  const { credit, source } = splitCredit(media.attribution ?? "");
  return (
    <div class={`media${compact ? " media--compact" : ""}`}>
      {media.kind === "image" && <img src={media.url} alt="" loading="lazy" />}
      {media.kind === "audio" && <audio controls preload="auto" src={media.url} />}
      {/* Muted autoplay is the only kind browsers allow without a click; controls let a clip with sound be unmuted. */}
      {media.kind === "video" && <video controls playsInline muted autoplay={autoplay} loop={loop} src={media.url} />}
      {!compact && credit.trim() && (
        <>
          {/* Not decoration: CC BY-SA requires the credit to travel with the picture. */}
          <p class="media__credit" dir="auto">{credit}</p>
          {source && <a class="media__credit media__source" href={source} target="_blank" rel="noopener nofollow">{t.t("media.source")}</a>}
        </>
      )}
    </div>
  );
}
