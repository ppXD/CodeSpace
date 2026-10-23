import { deriveSlug } from "@/components/agents/deriveSlug";

/** Shares the canonical ASCII slug derivation; empty names never receive a fallback. */
export function deriveExternalTriggerSlug(name: string): string {
  return deriveSlug(name);
}

export function isUsableExternalTriggerSlug(slug: string): boolean {
  return /^[a-z0-9_-]{1,64}$/.test(slug);
}
