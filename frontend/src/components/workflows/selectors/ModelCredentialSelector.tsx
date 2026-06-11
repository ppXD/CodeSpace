import { useModelCredentials } from "@/hooks/use-model-credentials";

interface ModelCredentialSelectorProps {
  /** Selected model-credential UUID ("" = none → fall back to the team/operator default). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Model-credential picker — lists the team's active model credentials (name + provider), saving the
 * chosen id. Used by the agent.code inspector to pick the key a run authenticates with; leaving it
 * empty falls back to the team default, then the operator-global key (the documented node > persona >
 * team > operator precedence). NEVER shows the secret — only the credential's name + provider.
 */
export function ModelCredentialSelector({ value, onChange }: ModelCredentialSelectorProps) {
  const creds = useModelCredentials();
  const rows = (creds.data ?? []).filter((c) => c.status === "Active");

  return (
    <select
      className="wf-form-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Model credential"
    >
      <option value="">{creds.isLoading ? "Loading…" : "Team / operator default"}</option>
      {rows.map((c) => <option key={c.id} value={c.id}>{c.displayName} ({c.provider})</option>)}
    </select>
  );
}
