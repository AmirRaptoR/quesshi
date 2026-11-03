import { useEffect } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp, type Me } from "./app";
import { GUEST_ROUTE_KEY, stored } from "./storage";

/**
 * Today's page gate: someone signed out goes to sign in and comes back here; a guest, on a page that
 * is not theirs, goes back to their one game. Returns the player once they may see the page.
 */
export function usePlayer(allowGuest = false): Me | null {
  const { me } = useApp();
  const { route, url } = useLocation();
  const allowed = me !== null && (allowGuest || !me.guest);
  useEffect(() => {
    if (!me) route(`/signin?next=${encodeURIComponent(url)}`, true);
    else if (!allowed) route(stored.get(GUEST_ROUTE_KEY) ?? "/signin", true);
  }, [me, allowed]);
  return allowed ? me : null;
}
