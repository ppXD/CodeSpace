import { useMemo } from "react";

import type { RepositorySummary } from "@/api/types";
import { useCredentials } from "@/hooks/use-credentials";
import { repositoryConnectionIssue, type ConnectionIssue } from "@/lib/credentialHealth";

/** What a repository row needs to say whether its connection works. A summary and a detail both fit. */
export type ConnectionRepository = Pick<RepositorySummary, "id" | "fullPath" | "providerInstanceId" | "credentialId" | "status" | "lastError">;

/**
 * Whether a repository's connection works, joined to the credential list — and the credential list's own loading state, so a
 * list that has not arrived yet never reads as a credential that is gone. One read shared by every screen that shows a repository.
 */
export function useRepositoryConnectionIssues() {
  const credentials = useCredentials();
  const byId = useMemo(() => new Map((credentials.data ?? []).map(c => [c.id, c] as const)), [credentials.data]);
  const loaded = credentials.data !== undefined;

  return {
    issueOf: (repository: ConnectionRepository): ConnectionIssue | null => repositoryConnectionIssue(repository, byId, loaded),
    credentialById: byId,
    loaded,
  };
}
