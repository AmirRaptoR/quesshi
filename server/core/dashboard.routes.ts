import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import { createDashboardStore } from "./dashboard.store";
import type { Db } from "./db";
import type { Rooms } from "./rooms";

/** The admin's front page: how big the site is and what is happening now. */
export function dashboardRoutes(auth: AdminAuth, db: Db, rooms: Rooms) {
  const store = createDashboardStore(db);
  return adminOnly(auth, "/api/admin").get("/dashboard", () => ({ ...store.counts(), openRooms: rooms.all().length }));
}
