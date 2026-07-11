import { useHarnesses } from "@/hooks/use-agents";

interface HarnessSelectorProps {
  /** Selected harness kind ("" = none chosen yet). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Harness picker for the `agent.run` node — the wire protocol the run speaks (e.g. codex-cli,
 * claude-code). Lists the harnesses registered in the engine; the saved value is the harness `kind`
 * string the node carries as `harness`.
 *
 * Used by the schema-driven form whenever a field declares `"x-selector": "harness"` — generic, not
 * tied to any one node.
 */
export function HarnessSelector({ value, onChange }: HarnessSelectorProps) {
  const harnesses = useHarnesses();
  const rows = harnesses.data ?? [];

  return (
    <select
      className="wf-form-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Harness"
    >
      <option value="">{harnesses.isLoading ? "Loading…" : "Pick a harness…"}</option>
      {rows.map((h) => <option key={h.kind} value={h.kind}>{h.kind}</option>)}
    </select>
  );
}
