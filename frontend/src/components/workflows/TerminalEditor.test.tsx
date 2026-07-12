import { useState } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { TerminalEditor } from "./TerminalEditor";
import type { WorkflowVariable } from "@/api/workflows";

// The {{}} picker is exercised elsewhere; a plain input keeps the focus on TerminalEditor's binding logic.
vi.mock("./VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) => (
    <input aria-label={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

const OUTPUTS: WorkflowVariable[] = [
  { name: "answer", label: "Answer", description: "The final result text", schema: {} },
  { name: "score", schema: {} },
];

/** Stateful harness so a binding edit round-trips through the controlled inputs bag, like the real editor. */
function Harness({ outputs = OUTPUTS, inputs: initInputs = {} }: { outputs?: WorkflowVariable[]; inputs?: Record<string, unknown> }) {
  const [inputs, setInputs] = useState<Record<string, unknown>>(initInputs);
  return <TerminalEditor outputs={outputs} inputs={inputs} onInputsChange={setInputs} suggestions={[]} />;
}

const row = (labelOrName: string) => screen.getByText(labelOrName).closest(".wf-form-row") as HTMLElement;

describe("TerminalEditor", () => {
  it("renders one binding row per declared output, using label when present, name otherwise", () => {
    render(<Harness inputs={{ answer: "{{nodes.solve.outputs.text}}" }} />);

    // label wins over name for the visible label; the saved binding shows in the picker.
    expect((within(row("Answer")).getByRole("textbox") as HTMLInputElement).value).toBe("{{nodes.solve.outputs.text}}");
    // no label ⇒ falls back to the raw name.
    expect((within(row("score")).getByRole("textbox") as HTMLInputElement).value).toBe("");
    // the description renders as help text.
    expect(screen.getByText("The final result text")).toBeTruthy();
  });

  it("binds an output to a value, merging into the inputs bag without disturbing siblings", () => {
    const onInputsChange = vi.fn();
    render(<TerminalEditor outputs={OUTPUTS} inputs={{ answer: "kept" }} onInputsChange={onInputsChange} suggestions={[]} />);

    fireEvent.change(within(row("score")).getByRole("textbox"), { target: { value: "{{nodes.grade.outputs.n}}" } });
    expect(onInputsChange).toHaveBeenLastCalledWith({ answer: "kept", score: "{{nodes.grade.outputs.n}}" });
  });

  it("clearing a binding drops the key (undefined) rather than writing an empty string", () => {
    const onInputsChange = vi.fn();
    render(<TerminalEditor outputs={OUTPUTS} inputs={{ answer: "x" }} onInputsChange={onInputsChange} suggestions={[]} />);

    fireEvent.change(within(row("Answer")).getByRole("textbox"), { target: { value: "" } });
    expect(onInputsChange).toHaveBeenLastCalledWith({ answer: undefined });
  });

  it("coerces a non-string saved binding to text for display (legacy literal bags)", () => {
    render(<Harness inputs={{ score: 42 }} />);

    expect((within(row("score")).getByRole("textbox") as HTMLInputElement).value).toBe("42");
  });

  it("shows a guidance hint (no rows) when the workflow declares no outputs", () => {
    render(<Harness outputs={[]} />);

    expect(screen.getByText(/declares no outputs yet/i)).toBeTruthy();
    expect(screen.queryByRole("textbox")).toBeNull();
  });
});
