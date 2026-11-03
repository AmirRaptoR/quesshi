import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import type { Rooms } from "./rooms";

/** Today's live monitor, for every format: what is open or running, and ending it as a no contest. */
export function roomAdminRoutes(auth: AdminAuth, rooms: Rooms) {
  return adminOnly(auth, "/api/admin/rooms")
    .get("/", () => rooms.all())
    .post("/:id/end", ({ params, set }) => {
      if (!rooms.endByAdmin(params.id)) {
        set.status = 404;
        return { error: "not_found" };
      }
      return { ok: true };
    });
}
