import type { CredentialSummary, RepositorySummary } from "@/api/types";

/**
 * What a credential's health says and what a repository running through it needs — the one place the lists, the Providers dialog
 * and the repository rows read it, so a credential never reads one way in one screen and another in the next.
 *
 * `health` comes from the backend and is derived there from the row (never stored); a server that predates it sends none, which is
 * why `status` is read alongside it: `Error` is the provider's refusal and `Revoked` is a disconnect, health or no health.
 */

export type CredentialHealthWord = "active" | "expiring" | "rejected" | "disconnected" | "owner_left";

/** The health word a credential's row says, folding `status` in for a server that sends no `health`. */
export function credentialHealthWord(credential: CredentialSummary): CredentialHealthWord {
  if (credential.status === "Revoked" || credential.health === "revoked") return "disconnected";
  if (credential.status === "Error" || credential.status === "Expired" || credential.health === "rejected") return "rejected";
  if (credential.health === "owner_left") return "owner_left";
  if (credential.health === "expiring") return "expiring";

  return "active";
}

/** The credential can authenticate as far as the row can tell: neither refused, disconnected nor lent by someone who left. */
export function isLiveCredential(credential: CredentialSummary): boolean {
  const word = credentialHealthWord(credential);

  return word === "active" || word === "expiring";
}

/** A pasted token (personal, project or group) rather than a sign-in — decides Replace token versus Reconnect. */
export function isStaticToken(credential: Pick<CredentialSummary, "authType">): boolean {
  return credential.authType === "Pat" || credential.authType === "ProjectAccessToken" || credential.authType === "GroupAccessToken";
}

/** What is wrong with a repository's connection, in the order a person would say it. */
export type ConnectionIssueKind = "unbound" | "retired" | "disconnected" | "rejected" | "owner_left" | "error";

export interface ConnectionIssue {
  kind: ConnectionIssueKind;
  /** The chip's words: short, lower-case, plain. */
  label: string;
  /** What the chip's tooltip adds: why, in a sentence. */
  title: string;
  /** The credential the repository runs through, when the list still has it. Absent when it is retired or the repository has none. */
  credential?: CredentialSummary;
}

/**
 * Why a repository's connection is not healthy, or null when it is. Joined by `credentialId`: a credential the list no longer holds
 * — retired when its person left, or its connection removed — is a disconnected one, not an absent one, and its repository is
 * exactly as stranded. Not only `status === "Error"`: a repository whose credential was refused or disconnected stays "Active"
 * until something flips it, and every run through it fails meanwhile.
 *
 * `credentialsLoaded` guards the join: before the credential list arrives, "not in the list" says nothing.
 */
export function repositoryConnectionIssue(repository: Pick<RepositorySummary, "credentialId" | "status" | "lastError">, credentialById: ReadonlyMap<string, CredentialSummary>, credentialsLoaded: boolean): ConnectionIssue | null {
  if (repository.status === "Paused") return null;

  if (!repository.credentialId) {
    return repository.status === "Error" ? { kind: "unbound", label: "needs new credential", title: repository.lastError ?? "This repository has no credential to authenticate with." } : null;
  }

  const credential = credentialById.get(repository.credentialId);

  if (!credential) {
    return credentialsLoaded ? { kind: "retired", label: "credential disconnected", title: "The credential this repository runs through is gone — retired when its owner left the team, or its connection was removed. Link the repository to one that works." } : errorOnly(repository);
  }

  switch (credentialHealthWord(credential)) {
    case "disconnected": return { kind: "disconnected", label: "credential disconnected", title: `“${credential.displayName}” was disconnected, so nothing bound through it can authenticate.`, credential };
    case "rejected": return { kind: "rejected", label: isStaticToken(credential) ? "needs a new token" : "needs reconnect", title: credential.lastError ?? `Its provider refused “${credential.displayName}”.`, credential };
    case "owner_left": return { kind: "owner_left", label: "owner left", title: `“${credential.displayName}” belongs to someone who left the team; it will be retired. Link the repository to a credential that stays.`, credential };
    default: return repository.status === "Error" ? { kind: "error", label: "needs new credential", title: repository.lastError ?? "This repository needs a credential that works.", credential } : null;
  }
}

function errorOnly(repository: Pick<RepositorySummary, "status" | "lastError">): ConnectionIssue | null {
  return repository.status === "Error" ? { kind: "error", label: "needs new credential", title: repository.lastError ?? "This repository needs a credential that works." } : null;
}
