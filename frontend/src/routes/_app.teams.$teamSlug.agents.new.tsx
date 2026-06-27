import { createFileRoute } from "@tanstack/react-router";

import { AgentEditor } from "@/components/agents/AgentEditor";

/** Author a new persona. Static `new` wins over the dynamic `$agentId` sibling. */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents/new")({
  component: NewAgentPage,
});

function NewAgentPage() {
  const { teamSlug } = Route.useParams();
  return <AgentEditor mode="create" teamSlug={teamSlug} />;
}
