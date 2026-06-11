import { createFileRoute } from "@tanstack/react-router";

import { SettingsLayout } from "@/components/settings/SettingsLayout";

/** Team Settings layout — team-scoped configuration (model credentials today; git providers next). */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings")({
  component: SettingsLayout,
});
