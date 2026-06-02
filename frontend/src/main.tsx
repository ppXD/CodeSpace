import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createRouter } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

// Variable font, bundled — no Google CDN dep. Geist Mono is used as the primary face
// across the whole product so character widths render the same on every device.
import "@fontsource-variable/geist-mono";

import { ActorIdentityProvider } from "./components/identities/ActorIdentityGate";
import { DialogProvider } from "./components/dialog";
import "./index.css";
import { routeTree } from "./routeTree.gen";

const router = createRouter({
  routeTree,
  defaultPreload: "intent",
  scrollRestoration: true,
});

// Register the router for TS — gives <Link>/<Navigate> typed `to` autocompletion.
declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      {/* ActorIdentityProvider sits below QueryClient so the link modal's mutation shares the
          cache. Any act-as-user action can route a 428 actor_identity_required into it to prompt
          a link + retry, app-wide. */}
      <ActorIdentityProvider>
        {/* DialogProvider sits below QueryClient so confirm/alert callers can use queries
            inside their flow (e.g. delete-then-show-error) without crossing context. */}
        <DialogProvider>
          <RouterProvider router={router} />
        </DialogProvider>
      </ActorIdentityProvider>
    </QueryClientProvider>
  </StrictMode>,
);
