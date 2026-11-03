import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import type { Clock } from "./clock";
import type { IdentityStore } from "./identity.store";

const whole = (value: string | undefined, fallback: number) => (value && /^\d+$/.test(value) ? Number(value) : fallback);

/** Today's player administration: search members and ban or unban them. */
export function userAdminRoutes(auth: AdminAuth, identity: IdentityStore, clock: Clock) {
  return adminOnly(auth, "/api/admin/users")
    .get("/", ({ query }) => {
      const { items, total } = identity.members(query.q?.trim() || undefined, whole(query.skip, 0), Math.min(Math.max(whole(query.take, 25), 1), 100));
      return {
        total,
        items: items.map((p) => ({ id: p.id, displayName: p.displayName, email: p.email, avatarSeed: p.avatarSeed, banned: p.banned, createdAt: p.createdAt })),
      };
    })
    .post("/:id/ban", ({ params, body, set }) => {
      const value = (body as { value?: unknown } | null)?.value === true;
      if (!identity.setBanned(params.id, value, clock.now().toISOString())) {
        set.status = 404;
        return { error: "not_found" };
      }
      return { ok: true };
    });
}
