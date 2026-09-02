/**
 * The pointer a destination uses to name its key: `db:<uuid>:<version>`.
 *
 * Parsed in one place because the version half is load-bearing and easy to lose. A destination names an EXACT key
 * version and the runtime never falls forward to a newer one, so "which key is this" and "which version of it" are
 * different questions — and a screen that answered the first by picking whichever key of the right provider happened
 * to be active would show the wrong key on any team that has more than one.
 */
export interface StorageCredentialReference {
  credentialId: string;
  revision: number;
}

export function parseCredentialRef(credentialRef: string | null | undefined): StorageCredentialReference | null {
  if (typeof credentialRef !== "string") return null;
  const parts = credentialRef.split(":");
  if (parts.length !== 3 || parts[0] !== "db" || parts[1].length === 0) return null;
  const revision = Number(parts[2]);
  return Number.isInteger(revision) && revision > 0 ? { credentialId: parts[1], revision } : null;
}

/** The key identity a pointer names, out of whatever metadata the screen already has. */
export function credentialForRef<T extends { id: string }>(credentialRef: string | null | undefined, credentials: readonly T[]): T | undefined {
  const reference = parseCredentialRef(credentialRef);
  return reference == null ? undefined : credentials.find((candidate) => candidate.id === reference.credentialId);
}
