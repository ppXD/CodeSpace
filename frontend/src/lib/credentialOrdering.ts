import type { CredentialSummary } from "@/api/types";

/**
 * Order credentials for the bind picker: TEAM-SERVICE first (the durable, preferred identity that
 * doesn't hinge on one person), then alphabetically. Stable + pure, so the picker can default-select
 * the top row and the choice is unit-testable without rendering.
 */
export function sortCredentialsByPreference(credentials: readonly CredentialSummary[]): CredentialSummary[] {
  return [...credentials].sort((a, b) => rank(a) - rank(b) || a.displayName.localeCompare(b.displayName));
}

function rank(credential: CredentialSummary): number {
  return credential.ownership === "TeamService" ? 0 : 1;
}

/** Short ownership label for a credential row: "Team service" vs the owner's name (else "Personal"). */
export function credentialOwnershipLabel(credential: CredentialSummary): string {
  return credential.ownership === "TeamService" ? "Team service" : credential.ownerUserName ?? "Personal";
}
