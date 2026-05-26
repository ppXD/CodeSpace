import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for `/teams/{slug}/workflows/*`. Per the existing repositories
 * pattern: TanStack treats this file as the parent of every `workflows.*` child,
 * so without an Outlet the index / detail / runs children can't render.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows")({
  component: () => <Outlet />,
});
