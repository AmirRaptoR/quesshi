/** localStorage that never throws: private windows and blocked storage just forget. */
export const stored = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Nothing to do: the value is a convenience.
    }
  },
};

/** Where a guest's one game is, so the front door sends them back to it. */
export const GUEST_ROUTE_KEY = "quesshi.guestRoute";
