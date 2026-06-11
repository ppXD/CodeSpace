import { createFileRoute } from "@tanstack/react-router";

import { ProvidersSettings } from "@/components/settings/ProvidersSettings";

/** Settings → Providers. The team's GitHub / GitLab integrations (same management as a project's "Connect remote"). */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings/providers")({
  component: ProvidersSettings,
});
