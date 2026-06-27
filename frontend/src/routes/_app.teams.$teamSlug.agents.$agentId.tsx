import { createFileRoute } from "@tanstack/react-router";

import { AgentEditor } from "@/components/agents/AgentEditor";

/** Edit an existing persona (the @handle is immutable). */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents/$agentId")({
  component: EditAgentPage,
});

function EditAgentPage() {
  const { teamSlug, agentId } = Route.useParams();
  return <AgentEditor mode="edit" teamSlug={teamSlug} agentId={agentId} />;
}
