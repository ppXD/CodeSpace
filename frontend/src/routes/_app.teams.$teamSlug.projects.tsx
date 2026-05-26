import { Outlet, createFileRoute } from "@tanstack/react-router";

/**
 * Pass-through layout for `/teams/{slug}/projects/*`. Project is a variable namespace,
 * not a workflow / repo container — see Phase 3.0 design (project.{slug}.X variable refs,
 * no FK from workflows/repos to projects).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/projects")({
  component: () => <Outlet />,
});
