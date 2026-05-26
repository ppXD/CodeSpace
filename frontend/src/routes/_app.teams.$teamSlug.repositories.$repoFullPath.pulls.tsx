import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pulls subtree layout. Pure pass-through — the PR list lives at the index child,
 * the PR detail at the `$number` child. Splitting them into separate routes gives
 * us shareable URLs for both views.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/pulls")({
  component: () => <Outlet />,
});
