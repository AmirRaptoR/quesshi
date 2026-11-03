import { useCallback, useEffect, useState } from "preact/hooks";
import { ApiError, get } from "./api";
import type { RoomView } from "./lobby";

/**
 * A room as this player sees it, kept current over the room's socket. A push carries what everyone
 * may see, so each one is followed by a read of this player's own view, as today's pages re-fetched.
 * `room` is undefined while loading and null when there is no such room.
 */
export function useRoom<T extends RoomView>(api: string, id: string | null) {
  const [room, setRoom] = useState<T | null | undefined>(undefined);
  const reload = useCallback(async () => {
    if (!id) return;
    try {
      setRoom(await get<T>(`${api}/${id}`));
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      setRoom(null);
    }
  }, [api, id]);

  useEffect(() => {
    void reload();
  }, [reload]);

  useEffect(() => {
    if (!id) return;
    const scheme = location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(`${scheme}://${location.host}${api}/${id}/socket`);
    // Without the socket the page still works; it just stops updating until reloaded, as today.
    socket.onmessage = () => void reload();
    return () => socket.close();
  }, [api, id, reload]);

  return { room, reload };
}
