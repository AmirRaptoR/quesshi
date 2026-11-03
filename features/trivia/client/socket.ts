/** A room's WebSocket, reopened after a drop so a phone that lost its network comes back on its own. */
export function roomSocket(path: string, handlers: { message(data: Record<string, unknown>): void; connected?(up: boolean): void }) {
  let socket: WebSocket | null = null;
  let closed = false;
  let retry: ReturnType<typeof setTimeout> | undefined;
  const open = () => {
    socket = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}${path}`);
    socket.onopen = () => handlers.connected?.(true);
    socket.onmessage = (event) => {
      try {
        handlers.message(JSON.parse(String(event.data)) as Record<string, unknown>);
      } catch {
        // A message that is not JSON is not ours to act on.
      }
    };
    socket.onclose = () => {
      if (closed) return;
      handlers.connected?.(false);
      retry = setTimeout(open, 1500);
    };
  };
  open();
  return {
    send: (message: unknown) => socket?.readyState === WebSocket.OPEN && socket.send(JSON.stringify(message)),
    close() {
      closed = true;
      clearTimeout(retry);
      socket?.close();
    },
  };
}
