import { useModelCredentials } from "@/hooks/use-model-credentials";

interface ModelCredentialSelectorProps {
  /** Selected model-credential UUID ("" = none → fall back to the team/operator default). */
  value: string;
  onChange: (next: string) => void;
  /**
   * Provider tags the chosen harness can drive (its SupportedProviders). When non-empty, the list shows ONLY
   * credentials for those providers — a credential the harness can't authenticate is filtered out per our
   * resolution logic, so it's never pickable. Unset/empty → no filter (every active credential is shown).
   */
  providers?: string[];
}

/**
 * Model-credential picker — lists the team's active model credentials (name + provider), saving the chosen
 * id. Filtered to the harness's drivable providers when given. Leaving it empty falls back to the team
 * default, then the operator-global key (node > persona > team > operator). NEVER shows the secret.
 */
export function ModelCredentialSelector({ value, onChange, providers }: ModelCredentialSelectorProps) {
  const creds = useModelCredentials();
  const active = (creds.data ?? []).filter((c) => c.status === "Active");

  const allow = providers && providers.length > 0
    ? new Set(providers.map((p) => p.toLowerCase()))
    : null;
  const shown = allow ? active.filter((c) => allow.has(c.provider.toLowerCase())) : active;

  // If the saved credential is now incompatible (e.g. the harness changed), keep it visible but flagged so the
  // field never silently blanks — the operator sees it needs re-picking. Resolution would reject it anyway.
  const selected = value ? active.find((c) => c.id === value) : undefined;
  const incompatible = selected && !shown.some((c) => c.id === selected.id) ? selected : undefined;

  return (
    <select
      className="wf-form-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Model credential"
    >
      <option value="">{creds.isLoading ? "Loading…" : "Team / operator default"}</option>
      {shown.map((c) => <option key={c.id} value={c.id}>{c.displayName} ({c.provider})</option>)}
      {incompatible && <option value={incompatible.id}>{incompatible.displayName} ({incompatible.provider}) — incompatible with this harness</option>}
    </select>
  );
}
