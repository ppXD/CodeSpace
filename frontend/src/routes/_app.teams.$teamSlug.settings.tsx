import { createFileRoute } from "@tanstack/react-router";

import { SettingsLayout } from "@/components/settings/SettingsLayout";

/** Team Settings layout — team-scoped model, Git provider, and storage configuration. */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings")({
  component: SettingsLayout,
});
