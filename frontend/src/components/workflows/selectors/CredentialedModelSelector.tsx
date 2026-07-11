import { useCredentialedModels, type CredentialedModelOption } from "@/hooks/use-model-credentials";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * Pickers for a `"x-selector": "credentialedModel"` field. The saved value is the credentialed-model ROW id
 * (`CredentialedModelOption.rowId`) — the (credential, model) handle the backend pool resolves by
 * (`ResolveByRowIdAsync`), NOT the bare model id or the credential id. Both the single and multi variants
 * render the shared {@link SearchSelect} combobox, so the "Lead model" picker looks and behaves exactly like
 * the "Allowed models" multi-select — only single-vs-multi differs.
 */
function toOption(m: CredentialedModelOption): SearchOption {
  const cred = `${m.credentialName} (${m.provider})`;
  return { id: m.rowId, label: m.modelId, meta: m.available === false ? `${cred} — offline` : cred };
}

/** Single credentialed-model picker. Value = the chosen model's `rowId`. */
export function CredentialedModelSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const models = useCredentialedModels();
  const options = (models.data ?? []).map(toOption);

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={models.isLoading}
      placeholder="Pick a model…"
    />
  );
}

/** Multi credentialed-model picker. Value = an array of `rowId`s. Empty = the whole team pool is allowed. */
export function CredentialedModelMultiSelector({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const models = useCredentialedModels();
  const options = (models.data ?? []).map(toOption);

  return (
    <SearchSelect
      multi
      options={options}
      value={value}
      onChange={onChange}
      loading={models.isLoading}
      placeholder="Search models…"
      hint={value.length === 0 ? "None selected — any of the team's models may be used." : `${value.length} selected — dispatched agents must use one of these.`}
    />
  );
}
