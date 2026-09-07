import type { TaskAcceptanceCompatibility, TaskSpecSuggestion } from "@/api/tasks";

/** Source assessment allows adoption, never a claim that the command passed. Legacy/unknown contracts remain proposals. */
export function sourceSupportedSpecChecks(suggestion: TaskSpecSuggestion | null | undefined): string[] {
  const proposal = suggestion?.acceptanceProposal;
  if (!proposal || proposal.version !== 1 || proposal.status !== "Supported" || !["user-explicit", "repository-evidence"].includes(proposal.source)) return [];
  if (!proposal.commandDigest || !proposal.sourceDigest || !proposal.evidence?.length || !Array.isArray(proposal.argv) || !proposal.argv.length) return [];
  if (proposal.argv.length !== suggestion!.acceptanceChecks.length || proposal.argv.some((token, i) => token !== suggestion!.acceptanceChecks[i])) return [];
  if (typeof proposal.argv[0] !== "string" || !proposal.argv[0].trim() || proposal.argv.some(token => typeof token !== "string" || token.includes("\0"))) return [];
  return proposal.argv;
}

export function adoptableSpecChecks(suggestion: TaskSpecSuggestion | null | undefined, compatibility: TaskAcceptanceCompatibility | null | undefined): string[] {
  return compatibility?.state === "Compatible" ? sourceSupportedSpecChecks(suggestion) : [];
}
