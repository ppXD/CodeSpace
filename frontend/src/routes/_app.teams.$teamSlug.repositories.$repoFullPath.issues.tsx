import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Issues subtree layout. Pure pass-through — the issue list lives at the index child, the issue detail
 * at the `$number` child. Splitting them gives shareable URLs for both and keeps the detail free of the
 * list's required search params (mirrors the Pulls subtree).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/issues")({
  component: () => <Outlet />,
});
