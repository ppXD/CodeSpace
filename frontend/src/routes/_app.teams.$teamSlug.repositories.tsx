import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for the `/teams/{slug}/repositories/*` subtree. TanStack's
 * file-based routing treats `repositories.tsx` as the PARENT route of every
 * `repositories.*` child file — without an Outlet here, the children
 * (index = list, $repoFullPath = detail) can't render at all. The actual list page
 * lives in the sibling `.index.tsx` file; this layout owns no UI of its own.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories")({
  component: () => <Outlet />,
});
