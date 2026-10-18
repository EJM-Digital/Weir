import type { ReactNode } from "react";
import type { RouteObject } from "react-router-dom";

type LazyLoader = () => Promise<object>;

/**
 * React Router never shows a route's own `errorElement` when that route's code fails to load, which is what an open tab
 * meets after an update replaced the files it points at: the route renders nothing, and the person sees a blank page. The
 * boundary has to come from somewhere that did load, so a lazy route gives it with its code: if the code arrives the
 * route keeps its own boundary for what goes wrong while it draws, and if it does not the nearest route above that
 * has one shows the error.
 */
export function boundariesThatSurviveALazyFailure(
  routes: RouteObject[],
): RouteObject[] {
  return routes.map((route) => {
    const children = route.children
      ? boundariesThatSurviveALazyFailure(route.children)
      : undefined;
    const { errorElement, lazy } = route;
    if (typeof lazy !== "function" || !errorElement) {
      return children ? ({ ...route, children } as RouteObject) : route;
    }
    const guarded = {
      ...route,
      ...(children ? { children } : {}),
      lazy: withBoundary(lazy as LazyLoader, errorElement),
    } as RouteObject;
    delete guarded.errorElement;
    return guarded;
  });
}

function withBoundary(load: LazyLoader, errorElement: ReactNode): LazyLoader {
  return async () => ({
    ...(await load()),
    ErrorBoundary: () => errorElement,
  });
}
