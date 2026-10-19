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

/**
 * A disconnected credential nothing leans on: no repository, group hook or act-as identity is left on it, so it is only history, and the
 * dialog folds it away and offers to remove it. Only a server that sends `dependentCount` can say so — one that predates it says nothing,
 * which is also the one that has no removal, so its rows are listed as they always were.
 */
export function isHistory(credential: CredentialSummary): boolean {
  return credentialHealthWord(credential) === "disconnected" && credential.dependentCount === 0;
}

/**
 * The credentials whose bindings `relink-bindings` moves in one action: a disconnected or refused credential that still holds repositories or group hooks
 * (the two it carries — a hook-only credential moves too). Not one still in service, whatever it reads — the server refuses a source that is `Active` (a Disconnect that
 * names where they go is how those are moved), so a token whose expiry passed but whose row is not yet marked is not offered a move that could only answer 400.
 * Not a credential held only by act-as identities: the move leaves those where they are.
 */
export function canMoveBindingsOff(credential: CredentialSummary): boolean {
  const word = credentialHealthWord(credential);

  return (word === "disconnected" || word === "rejected") && credential.status !== "Active" && ((credential.boundRepositoryCount ?? 0) > 0 || (credential.boundConnectionWebhookCount ?? 0) > 0);
}

/**
 * What leans on a credential, in a few words. A working one says what a reconnect or a disconnect puts at stake — "3 repositories bound". A dead one says why it is
 * still listed — "3 repositories still use it", or with the group hooks and act-as identities that hold it as well ("2 repositories and 1 group hook still use it") — so
 * a row held only by a hook still gives its reason. Null when nothing is on it (an older server's missing counts too).
 */
export function dependentsLabel(credential: CredentialSummary): string | null {
  const repositories = credential.boundRepositoryCount ?? 0;
  const word = credentialHealthWord(credential);

  if (word !== "disconnected" && word !== "rejected") return repositories > 0 ? `${repositories} ${repositories === 1 ? "repository" : "repositories"} bound` : null;

  const kinds = [
    { count: repositories, one: "repository", many: "repositories" },
    { count: credential.boundConnectionWebhookCount ?? 0, one: "group hook", many: "group hooks" },
    { count: credential.actAsIdentityCount ?? 0, one: "act-as identity", many: "act-as identities" },
  ].filter(k => k.count > 0);

  if (kinds.length === 0) return null;

  const parts = kinds.map(k => `${k.count} ${k.count === 1 ? k.one : k.many}`);
  const subject = parts.length === 1 ? parts[0] : `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;

  return `${subject} ${kinds.length === 1 && kinds[0].count === 1 ? "still uses" : "still use"} it`;
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
