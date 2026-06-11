import { useState } from "react";

import { useHarnesses } from "@/hooks/use-agents";

import type { ScopeSuggestion } from "./scope-introspection";
import { VariablePickerInput } from "./VariablePickerInput";
import { AgentSelector } from "./selectors/AgentSelector";
import { HarnessSelector } from "./selectors/HarnessSelector";
import { ModelCredentialSelector } from "./selectors/ModelCredentialSelector";
import { ProjectRepositorySelector } from "./selectors/ProjectRepositorySelector";

export interface AgentCodeInspectorProps {
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
  onInputsChange: (next: Record<string, unknown>) => void;
  suggestions: ScopeSuggestion[];
}

const str = (v: unknown) => (typeof v === "string" ? v : "");

/**
 * Dedicated inspector for the `agent.code` node — replaces the generic Config/Inputs forms with the
 * two ways to run a coding agent the engine already supports:
 *
 *   • Use an Agent persona — bind a persona (its system prompt + model + tools become the defaults);
 *     the goal is an optional task-specific addition, and model / credential can override the persona.
 *   • Configure inline — no persona; set the goal + model + credential directly.
 *
 * The harness is required either way (personas are harness-agnostic), so it sits above the toggle.
 * Switching to "inline" clears the bound persona so the saved config matches the chosen mode.
 */
export function AgentCodeInspector({ config, inputs, onConfigChange, onInputsChange, suggestions }: AgentCodeInspectorProps) {
  const harness = str(config.harness);
  const agentId = str(config.agentDefinitionId);
  const goal = str(config.goal);
  const model = str(config.model);
  const credentialId = str(config.modelCredentialId);
  const repositoryId = str(inputs.repositoryId);
  const timeoutSeconds = typeof config.timeoutSeconds === "number" ? config.timeoutSeconds : undefined;
  const network = config.network === true;
  const readOnly = config.readOnly === true;

  // Mode is seeded from whether a persona is bound, with a local override so the operator can switch
  // to inline (clearing the persona) or back to a persona freely.
  const [mode, setMode] = useState<"agent" | "inline">(agentId ? "agent" : "inline");

  const harnesses = useHarnesses();
  const selectedHarness = harnesses.data?.find((h) => h.kind === harness);
  const modelHints = selectedHarness?.models ?? [];
  const credProviders = selectedHarness?.supportedProviders ?? [];

  // Set string/number keys, deleting blanks so the saved config stays minimal (the engine applies its
  // own defaults for absent keys — model → persona/harness default, timeoutSeconds → 1800, etc.).
  const patch = (p: Record<string, unknown>) => {
    const next = { ...config, ...p };
    for (const k of Object.keys(p)) {
      const v = next[k];
      if (v === "" || v === undefined || v === null) delete next[k];
    }
    onConfigChange(next);
  };

  const setBool = (key: string, on: boolean) => {
    const next = { ...config };
    if (on) next[key] = true; else delete next[key];
    onConfigChange(next);
  };

  const setTimeout = (raw: string) => {
    const next = { ...config };
    const n = Math.floor(Number(raw));
    if (raw.trim() === "" || !Number.isFinite(n) || n < 1) delete next.timeoutSeconds;
    else next.timeoutSeconds = n;
    onConfigChange(next);
  };

  return (
    <>
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Harness</div>
        <div className="wf-form-row">
          <HarnessSelector value={harness} onChange={(v) => patch({ harness: v })} />
          <span className="wf-form-help">The wire protocol the run speaks. Required whether or not you bind an Agent.</span>
        </div>
      </section>

      <section className="wf-inspector-section">
        <div className="cn-tabs cn-tabs-inline" role="tablist" aria-label="How to configure the run">
          <button type="button" className="cn-tab" role="tab" aria-selected={mode === "agent"} data-active={mode === "agent"} onClick={() => setMode("agent")}>Use an Agent</button>
          <button type="button" className="cn-tab" role="tab" aria-selected={mode === "inline"} data-active={mode === "inline"} onClick={() => { setMode("inline"); patch({ agentDefinitionId: "" }); }}>Configure inline</button>
        </div>

        {mode === "agent" ? (
          <>
            <label className="wf-form-row">
              <span className="wf-form-label">Agent persona</span>
              <AgentSelector value={agentId} onChange={(v) => patch({ agentDefinitionId: v })} />
            </label>

            <label className="wf-form-row">
              <span className="wf-form-label">Additional instructions</span>
              <VariablePickerInput value={goal} onChange={(v) => patch({ goal: v })} suggestions={suggestions} multiline placeholder="Task-specific addition to the persona's prompt (optional)" />
            </label>

            <p className="wf-form-help">The persona supplies the system prompt, model, and tools. Override the model or credential below if needed.</p>
          </>
        ) : (
          <label className="wf-form-row">
            <span className="wf-form-label">Instructions</span>
            <VariablePickerInput value={goal} onChange={(v) => patch({ goal: v })} suggestions={suggestions} multiline placeholder="What should the agent do?" />
          </label>
        )}

        <label className="wf-form-row">
          <span className="wf-form-label">{mode === "agent" ? "Model override" : "Model"}</span>
          <input
            className="wf-form-input"
            list="agentcode-model-hints"
            value={model}
            onChange={(e) => patch({ model: e.target.value })}
            placeholder={mode === "agent" ? "Leave blank to use the persona's model" : "Leave blank for the harness default"}
            spellCheck={false}
          />
          {modelHints.length > 0 && <datalist id="agentcode-model-hints">{modelHints.map((m) => <option key={m} value={m} />)}</datalist>}
        </label>

        <label className="wf-form-row">
          <span className="wf-form-label">{mode === "agent" ? "Model credential override" : "Model credential"}</span>
          <ModelCredentialSelector value={credentialId} onChange={(v) => patch({ modelCredentialId: v })} providers={credProviders} />
          {harness
            ? credProviders.length > 0 && <span className="wf-form-help">Only keys the <code>{harness}</code> harness can use ({credProviders.join(" / ")}) are shown. Empty = the team / operator default.</span>
            : <span className="wf-form-help">Pick a harness first to filter compatible keys.</span>}
        </label>
      </section>

      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Run settings</div>

        <label className="wf-form-row">
          <span className="wf-form-label">Repository</span>
          <ProjectRepositorySelector value={repositoryId} onChange={(v) => onInputsChange({ ...inputs, repositoryId: v === "" ? undefined : v })} />
          <span className="wf-form-help">Cloned into the agent's workspace before it runs. Leave empty for an analysis-only run.</span>
        </label>

        <label className="wf-form-row">
          <span className="wf-form-label">Timeout (seconds)</span>
          <input
            className="wf-form-input"
            type="number"
            min={1}
            value={timeoutSeconds ?? ""}
            onChange={(e) => setTimeout(e.target.value)}
            placeholder="1800"
          />
        </label>

        <label className="wf-form-check wf-form-check-field">
          <input type="checkbox" className="wf-form-checkbox" checked={network} onChange={(e) => setBool("network", e.target.checked)} />
          <span className="wf-form-check-text">
            <span className="wf-form-check-label">Allow network access</span>
            <span className="wf-form-help">The sandbox can reach the network during the run.</span>
          </span>
        </label>

        <label className="wf-form-check wf-form-check-field">
          <input type="checkbox" className="wf-form-checkbox" checked={readOnly} onChange={(e) => setBool("readOnly", e.target.checked)} />
          <span className="wf-form-check-text">
            <span className="wf-form-check-label">Analysis only (read-only)</span>
            <span className="wf-form-help">The agent may read the repo but not write changes.</span>
          </span>
        </label>
      </section>
    </>
  );
}
