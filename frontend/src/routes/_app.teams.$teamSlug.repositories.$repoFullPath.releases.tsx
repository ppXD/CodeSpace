import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Releases subtree layout. Pure pass-through — the releases/tags list lives at the index child, the
 * single-release detail at the `$tag` child. Splitting them gives shareable URLs for both and keeps the
 * detail free of the list's required search params (mirrors the Pulls / Issues subtrees).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/releases")({
  component: () => <Outlet />,
});
