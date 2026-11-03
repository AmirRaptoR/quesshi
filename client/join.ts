/**
 * Where a guest opening /join/:code belongs instead of the join card: back in their own game, but
 * only when the code names that very room, so a stale memory never swallows another room's link.
 */
export function guestReturn(isGuest: boolean, stored: string | null, room: { id: string }, lobbyPath: string): string | null {
  if (!isGuest || !stored) return null;
  return stored === lobbyPath || stored.endsWith(`/${room.id}`) ? stored : null;
}

/** The server's rule for a display name, checked here only to keep a doomed request from being sent. */
export const usableName = (name: string): boolean => name.trim().length >= 2 && name.trim().length <= 24;
