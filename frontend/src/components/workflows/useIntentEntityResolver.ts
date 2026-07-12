import { useMemo } from "react";

import { useAgentDefinitions } from "@/hooks/use-agents";
import { useConversations } from "@/hooks/use-chat";
import { useCredentialedModels, useModelCredentials } from "@/hooks/use-model-credentials";
import { useRepositories } from "@/hooks/use-repositories";
import { useTeamMemberIdentities } from "@/hooks/use-team-members";

import type { EntityResolution, IntentResolver } from "./intentCompose";

/**
 * Resolves a stored entity id → its friendly display NAME for the IntentLine, reusing the SAME cached
 * React-Query hooks the x-selector pickers use (read-only, so it adds no fetches). Each kind maps by the
 * exact id the selector stores. A name is returned ONLY on a positive catalog hit; while a catalog loads
 * or an id has no match, status is loading/unresolved so the composer shows a muted placeholder — a raw
 * GUID must never surface. A harness value is already its friendly kind string, and any kind without a
 * table renders verbatim.
 */
export function useIntentEntityResolver(): IntentResolver {
  const repos = useRepositories();
  const models = useCredentialedModels();
  const creds = useModelCredentials();
  const agents = useAgentDefinitions();
  const convos = useConversations();
  const users = useTeamMemberIdentities();

  return useMemo(() => {
    const table: Record<string, { map: Map<string, string>; loading: boolean }> = {
      repository: { map: new Map((repos.data ?? []).map((r) => [r.id, r.fullPath])), loading: repos.isLoading },
      credentialedModel: { map: new Map((models.data ?? []).map((m) => [m.rowId, m.modelId])), loading: models.isLoading },
      modelCredential: { map: new Map((creds.data ?? []).map((c) => [c.id, c.displayName])), loading: creds.isLoading },
      agent: { map: new Map((agents.data ?? []).map((a) => [a.id, a.name || `@${a.slug}`])), loading: agents.isLoading },
      conversation: { map: new Map((convos.data ?? []).map((c) => [c.id, c.kind === "Channel" ? `#${c.slug ?? c.name ?? ""}` : (c.name || "(direct message)")])), loading: convos.isLoading },
      user: { map: new Map((users.data ?? []).map((u) => [u.userId, u.name])), loading: users.isLoading },
    };

    return {
      resolve(kind: string, id: string): EntityResolution {
        const entry = table[kind];
        if (!entry) return { status: "resolved", name: id }; // harness kind / bare model string / unknown → verbatim
        const hit = entry.map.get(id);
        if (hit != null && hit !== "") return { status: "resolved", name: hit };
        return entry.loading ? { status: "loading" } : { status: "unresolved" };
      },
    };
  }, [
    repos.data, repos.isLoading, models.data, models.isLoading, creds.data, creds.isLoading,
    agents.data, agents.isLoading, convos.data, convos.isLoading, users.data, users.isLoading,
  ]);
}
