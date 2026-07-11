import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRouter } from "@tanstack/react-router";
import { render, waitFor } from "@testing-library/react";
import { expect, vi } from "vitest";

import { routeTree } from "@/routeTree.gen";

/**
 * Route-component test harness. Mounts the REAL generated route tree in a memory router at a given
 * URL, so route components exercise their actual param parsing, loaders, `beforeLoad` redirects, and
 * the team-scope gate exactly as production does. The backend is a URL-keyed `fetch` stub, so the
 * real api/hook layer runs end-to-end.
 *
 * The caller must stub `@/components/AppShell` to a bare `<Outlet/>` (the shell fires its own heavy
 * query fan-out and isn't under test) — see the tests in this folder for the one-liner.
 */

/** Stub `global.fetch`: the first key that the request URL contains wins; an unmatched URL is a 404. */
export function stubFetch(routes: Record<string, unknown>): void {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    const key = Object.keys(routes).find((k) => url.includes(k));
    const body = key === undefined ? undefined : routes[key];
    return new Response(body === undefined ? "" : JSON.stringify(body), {
      status: body === undefined ? 404 : 200,
      headers: { "Content-Type": "application/json" },
    });
  }));
}

export async function renderRoute(initialUrl: string) {
  // Pass the _app auth gate (isAuthenticated() reads this).
  localStorage.setItem("codespace.jwt", "test-jwt");

  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });

  const router = createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [initialUrl] }),
  });

  const utils = render(
    <QueryClientProvider client={queryClient}>
      {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
      <RouterProvider router={router as any} />
    </QueryClientProvider>,
  );

  // Let the router resolve the initial match + any beforeLoad redirect settle.
  await waitFor(() => expect(router.state.status).toBe("idle"));

  return { router, currentPath: () => router.state.location.pathname, ...utils };
}
