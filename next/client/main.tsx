import { render, type ComponentType } from "preact";
import { lazy, LocationProvider, Route, Router } from "preact-iso";
import { AppProvider } from "./app";
import { clientFeatures } from "./features";
import type { PageProps } from "./feature";
import { Layout, NotFound } from "./layout";
import { Landing } from "./pages/landing";
import { Join } from "./pages/join";
import { Play } from "./pages/play";
import { Profile } from "./pages/profile";
import { Settings } from "./pages/settings";
import { SignIn } from "./pages/signin";

// The admin is a chunk of its own: players never download it.
const Admin = lazy(() => import("./admin/routes").then((module) => module.AdminRouter));

// preact-iso types a route's props from a literal path; these paths come from a list, so the
// router is given the one shape every page takes.
const PageRoute = Route as unknown as ComponentType<{ path: string; component: ComponentType<PageProps> }>;

function App() {
  return (
    <AppProvider>
      <LocationProvider>
        <Layout>
          <Router>
            <Route path="/" component={Landing} />
            <Route path="/signin" component={SignIn} />
            <Route path="/admin" component={Admin} />
            <Route path="/admin/*" component={Admin} />
            <Route path="/play" component={Play} />
            <PageRoute path="/join/:code" component={Join} />
            <Route path="/settings" component={Settings} />
            <Route path="/profile" component={Profile} />
            {clientFeatures.flatMap((feature) => feature.routes.map((route) => <PageRoute key={route.path} path={route.path} component={route.page} />))}
            <Route default component={NotFound} />
          </Router>
        </Layout>
      </LocationProvider>
    </AppProvider>
  );
}

render(<App />, document.getElementById("app")!);
