import { createFileRoute } from "@tanstack/react-router";

import { ConnectedIdentities } from "@/components/identities/ConnectedIdentities";

/** `/teams/{slug}/identities` — the caller's connected provider identities (Model B). Renders inside
 *  the team shell; team scope (X-Team-Id) drives which provider instances are listed. */
export const Route = createFileRoute("/_app/teams/$teamSlug/identities")({
  component: ConnectedIdentities,
});
