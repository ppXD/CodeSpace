import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for `/teams/{slug}/agents/*` — the Agents library (reusable personas).
 * Mirrors the workflows layout: TanStack treats this file as the parent of every `agents.*`
 * child, so the index (and later detail) children need an Outlet here to render.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents")({
  component: () => <Outlet />,
});
