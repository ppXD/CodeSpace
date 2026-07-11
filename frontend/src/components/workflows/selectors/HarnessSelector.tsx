import { useHarnesses } from "@/hooks/use-agents";

import { SearchSelect } from "./SearchSelect";

/**
 * Harness picker (`"x-selector": "harness"`) — the coding-agent CLI the run speaks (e.g. codex-cli,
 * claude-code). The saved value is the harness `kind` string. Renders the shared {@link SearchSelect}
 * combobox so it matches every other dropdown.
 */
export function HarnessSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const harnesses = useHarnesses();
  const options = (harnesses.data ?? []).map((h) => ({ id: h.kind, label: h.kind }));

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={harnesses.isLoading}
      placeholder="Pick a harness…"
    />
  );
}
