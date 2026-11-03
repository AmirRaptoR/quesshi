import type { ComponentType } from "preact";
import { useApp } from "../../../../client/app";
import type { PageProps } from "../../../../client/feature";
import { usePlayer } from "../../../../client/player";

/** A page shown only to who may see it; anyone else is sent on by the core's gate, as today. */
export function gated(Page: ComponentType<PageProps>, allowGuest: boolean): ComponentType<PageProps> {
  return function Gated(props: PageProps) {
    const { t } = useApp();
    return usePlayer(allowGuest) ? <Page {...props} /> : <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  };
}
