import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for `/teams/{slug}/workflows/{id}/*`. Both the canvas (index)
 * and the runs sub-page (`.runs.tsx`) render here via <Outlet/>. Owning a thin layout
 * keeps the URL hierarchy explicit even though the layout itself adds no chrome —
 * the canvas owns its own top bar, the runs page uses the standard `.ct` shell.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/$workflowId")({
  component: () => <Outlet />,
});
