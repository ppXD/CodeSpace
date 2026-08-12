import { createFileRoute } from "@tanstack/react-router";

import { MembersSettings } from "@/components/settings/MembersSettings";

/** Settings → Members. Who is in the team, what they may do, and who has been offered a seat. */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings/members")({
  component: MembersSettings,
});
