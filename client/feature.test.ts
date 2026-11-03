import { describe, expect, test } from "bun:test";
import { orderedRoutes, type ClientFeature } from "./feature";

const page = () => null;
const feature = (name: string, paths: string[]): ClientFeature => ({ name, routes: paths.map((path) => ({ path, page })) });

describe("the features' pages", () => {
  test("put fixed addresses before ones with a named part, keeping each group's order", () => {
    const routes = orderedRoutes([feature("trivia", ["/play/questions", "/play/:id", "/lobby/:code"]), feature("polls", ["/play/voting", "/voting/:id"])]);

    expect(routes.map((route) => route.path)).toEqual(["/play/questions", "/play/voting", "/play/:id", "/lobby/:code", "/voting/:id"]);
  });
});
