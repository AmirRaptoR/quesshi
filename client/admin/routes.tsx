import type { ComponentType } from "preact";
import { useLocation } from "preact-iso";
import { NotFound } from "../layout";
import { AdminForgot, AdminLogin, AdminPassword, AdminReset } from "./auth-pages";
import { AdminCategories } from "./categories";
import { AdminAccounts, AdminDashboard, AdminLive, AdminReported, AdminUsers } from "./pages";
import { AdminQuestions } from "./questions";

const PAGES: Readonly<Record<string, ComponentType>> = {
  "/admin": AdminDashboard,
  "/admin/login": AdminLogin,
  "/admin/forgot": AdminForgot,
  "/admin/reset": AdminReset,
  "/admin/password": AdminPassword,
  "/admin/accounts": AdminAccounts,
  "/admin/categories": AdminCategories,
  "/admin/questions": AdminQuestions,
  "/admin/reported": AdminReported,
  "/admin/users": AdminUsers,
  "/admin/live": AdminLive,
};

/** The admin panel, loaded as one chunk the first time an admin address opens. */
export function AdminRouter() {
  const { path } = useLocation();
  const Page = PAGES[path.replace(/\/+$/, "") || "/admin"] ?? NotFound;
  return <Page />;
}
