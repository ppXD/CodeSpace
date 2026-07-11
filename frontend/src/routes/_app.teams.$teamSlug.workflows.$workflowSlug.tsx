import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for `/teams/{slug}/workflows/{id}/*`. The agent detail page (index)
 * renders here via <Outlet/>. Owning a thin layout keeps the URL hierarchy explicit even
 * though the layout itself adds no chrome — the detail page brings its own tab shell.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/$workflowSlug")({
  component: () => <Outlet />,
});
