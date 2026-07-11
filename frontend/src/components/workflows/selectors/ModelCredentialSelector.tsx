import { useModelCredentials } from "@/hooks/use-model-credentials";

import { SearchSelect, type SearchOption } from "./SearchSelect";

interface ModelCredentialSelectorProps {
  /** Selected model-credential UUID ("" = none → fall back to the team/operator default). */
  value: string;
  onChange: (next: string) => void;
  /** Provider tags the chosen harness can drive; when non-empty, only matching credentials are offered. */
  providers?: string[];
}

/**
 * Model-credential picker (`"x-selector": "modelCredential"`) — the owning credential each agent
 * authenticates with. Saves the credential id; empty = the team/operator default. Filtered to the harness's
 * drivable providers when given. A now-incompatible saved credential stays visible (flagged) so the field
 * never silently blanks. Renders the shared {@link SearchSelect} combobox to match every other dropdown.
 */
export function ModelCredentialSelector({ value, onChange, providers }: ModelCredentialSelectorProps) {
  const creds = useModelCredentials();
  const active = (creds.data ?? []).filter((c) => c.status === "Active");

  const allow = providers && providers.length > 0 ? new Set(providers.map((p) => p.toLowerCase())) : null;
  const shown = allow ? active.filter((c) => allow.has(c.provider.toLowerCase())) : active;

  const options: SearchOption[] = shown.map((c) => ({ id: c.id, label: c.displayName, meta: c.provider }));

  // If the saved credential is now incompatible (harness changed), keep it visible + flagged. The flag goes
  // in the LABEL (not meta) so it shows on the selected chip, not just in the dropdown row.
  const selected = value ? active.find((c) => c.id === value) : undefined;
  if (selected && !shown.some((c) => c.id === selected.id)) {
    options.push({ id: selected.id, label: `${selected.displayName} — incompatible with this harness`, meta: selected.provider });
  }

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={creds.isLoading}
      placeholder="Team / operator default"
    />
  );
}
