import { createFileRoute, redirect } from "@tanstack/react-router";

/** Bare /settings → its first section. */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings/")({
  beforeLoad: ({ params }) => {
    throw redirect({ to: "/teams/$teamSlug/settings/model-credentials", params: { teamSlug: params.teamSlug } });
  },
});
