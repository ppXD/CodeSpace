import type { ProviderKind } from "@/api/types";

/**
 * Whether a team-service credential can be created for this provider through the UI today.
 *
 * Team-service credentials are a per-provider MECHANISM, not one universal flow:
 *   • GitLab — a paste-able Group Access Token (owned by the group, not a person). Supported now.
 *   • GitHub — the equivalent is a GitHub App installation, a different flow that isn't built yet.
 *   • Git    — a bare remote has no team identity to mint a token from.
 *
 * Centralised here so the Team tab, its per-provider add affordance, and any future provider all
 * agree on the same policy — when GitHub App lands, this is the single place that opens it up.
 */
export function providerSupportsTeamServiceCredential(provider: ProviderKind): boolean {
  return provider === "GitLab";
}
