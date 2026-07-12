import { useCredentialedModels, type CredentialedModelOption } from "@/hooks/use-model-credentials";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * The Model picker for a `"x-selector": "poolModel"` field (llm.complete's `model`). Lists the team's ENABLED
 * pool models for the SIBLING `provider`, and stores the bare MODEL-ID string — exactly what the backend pins
 * on (`ModelPoolSelector` matches `ModelId.ToLower()`), so a picked model is byte-identical to a hand-typed one
 * and fully non-breaking. Deliberately NOT the `credentialedModel` selector: that stores a (credential, model)
 * ROW id, which the model-id pin could never match — so this field needs its own control.
 *
 * Empty value = "Auto" (the pool picks its recommended model). renderControl wraps this scalar selector in the
 * Pick ⇄ Expression toggle, so an author can still bind the model to a `{{ref}}` or hand-type an id the pool
 * doesn't list yet — the latter is also surfaced here as a "not in this pool" row so a pre-existing value never
 * silently vanishes.
 */

// The provider a blank sibling resolves to at run time: LlmCompleteNode reads config "provider" with an
// "Anthropic" fallback, so an unset provider field scopes the model list to Anthropic — matching execution.
const DEFAULT_PROVIDER = "Anthropic";

const isRef = (s: string) => s.includes("{{");

function toOption(m: CredentialedModelOption): SearchOption {
  return { id: m.modelId, label: m.modelId, meta: m.available === false ? `${m.credentialName} — offline` : m.credentialName };
}

export function PoolModelSelector({ provider, value, onChange }: { provider?: string; value: string; onChange: (next: string) => void }) {
  const models = useCredentialedModels();

  // A dynamic {{ref}} provider can't scope a static list — prompt for Expression instead of showing a wrong pool.
  const dynamicProvider = typeof provider === "string" && isRef(provider);
  const effectiveProvider = dynamicProvider ? null : provider && provider.trim() !== "" ? provider : DEFAULT_PROVIDER;

  const forProvider = effectiveProvider == null ? [] : (models.data ?? []).filter((m) => m.provider === effectiveProvider);

  // Dedup by model id (the same model can ride two credentials of one provider; the pin resolves either).
  const seen = new Set<string>();
  const options: SearchOption[] = [];
  for (const m of forProvider) {
    const key = m.modelId.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    options.push(toOption(m));
  }

  // Keep a pre-existing hand-typed / since-removed model resolvable so its chip shows the id (not "Unavailable")
  // rather than the field silently blanking. SearchSelect hides the selected id from the dropdown, so no meta.
  const valueMissing = !!value && !isRef(value) && !dynamicProvider && !seen.has(value.toLowerCase());
  if (valueMissing) options.push({ id: value, label: value });

  const hint = dynamicProvider
    ? "The provider is a dynamic reference — switch to Expression to set the model, or leave empty for Auto."
    : valueMissing
      ? `"${value}" isn't in ${effectiveProvider}'s enabled pool — it'll still be sent as the pin, or pick another below.`
      : effectiveProvider && !models.isLoading && options.length === 0
        ? `No enabled models under ${effectiveProvider} yet — leave empty for Auto, or add one under Model credentials.`
        : "Pins one model. Leave empty to let the pool pick its recommended model.";

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={models.isLoading}
      placeholder={dynamicProvider ? "Provider is dynamic…" : "Pick a model — or leave empty for Auto"}
      hint={hint}
    />
  );
}
