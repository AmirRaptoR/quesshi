import { render, type ComponentType } from "preact";
import { LocationProvider, Route, Router } from "preact-iso";
import { AppProvider } from "./app";
import { clientFeatures } from "./features";
import type { PageProps } from "./feature";
import { Layout, NotFound } from "./layout";

// preact-iso types a route's props from a literal path; these paths come from a list, so the
// router is given the one shape every page takes.
const PageRoute = Route as unknown as ComponentType<{ path: string; component: ComponentType<PageProps> }>;

function App() {
  return (
    <AppProvider>
      <LocationProvider>
        <Layout>
          <Router>
            {clientFeatures.flatMap((feature) => feature.routes.map((route) => <PageRoute key={route.path} path={route.path} component={route.page} />))}
            <Route default component={NotFound} />
          </Router>
        </Layout>
      </LocationProvider>
    </AppProvider>
  );
}

render(<App />, document.getElementById("app")!);
