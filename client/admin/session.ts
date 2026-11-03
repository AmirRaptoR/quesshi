import { useEffect, useState } from "preact/hooks";
import { ApiError, get, post } from "../api";
import { sessionOutcome } from "./logic";

/** The signed-in administrator, as /api/admin/auth/me returns them. */
export interface AdminMe { id: string; username: string; email: string; mustChangePassword: boolean }

export type AdminSession =
  | { status: "checking" }
  | { status: "signed_out" }
  | { status: "unconfirmed"; retrying: boolean }
  | { status: "signed_in"; admin: AdminMe };

// Kept apart from the player's session, as today: a different cookie, and signing out of one leaves the other.
let session: AdminSession = { status: "checking" };
let checked = false;
let generation = 0;
const listeners = new Set<() => void>();

function set(next: AdminSession) {
  session = next;
  for (const listener of listeners) listener();
}

async function check() {
  const started = generation;
  try {
    const admin = await get<AdminMe>("/api/admin/auth/me");
    if (started === generation) set({ status: "signed_in", admin });
  } catch (error) {
    // A sign-out while this call was in flight must not be undone by its late answer.
    if (started !== generation) return;
    set(sessionOutcome(error) === "signed_out" ? { status: "signed_out" } : { status: "unconfirmed", retrying: false });
  }
}

export const adminSession = {
  /** Asks the server once per visit who is signed in; later pages reuse the answer. */
  ensure(): void {
    if (checked) return;
    checked = true;
    void check();
  },
  async retry(): Promise<void> {
    if (session.status !== "unconfirmed" || session.retrying) return;
    set({ status: "unconfirmed", retrying: true });
    await check();
  },
  signedIn(admin: AdminMe): void {
    checked = true;
    generation++;
    set({ status: "signed_in", admin });
  },
  /** Reloads the admin after a change to their own account. */
  async reload(): Promise<void> {
    try {
      set({ status: "signed_in", admin: await get<AdminMe>("/api/admin/auth/me") });
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
    }
  },
  async signOut(): Promise<void> {
    generation++;
    await post("/api/admin/auth/sign-out").catch(() => {});
    set({ status: "signed_out" });
  },
};

export function useAdminSession(): AdminSession {
  const [, redraw] = useState(0);
  useEffect(() => {
    const listener = () => redraw((n) => n + 1);
    listeners.add(listener);
    adminSession.ensure();
    return () => listeners.delete(listener);
  }, []);
  return session;
}
