import { useState } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MapEditor } from "./MapEditor";
import { resultKeyError } from "./mapResultKey";
import type { ScopeSuggestion } from "./scope-introspection";

// The {{}} picker is exercised elsewhere; here a plain input keeps the focus on MapEditor's logic.
vi.mock("./VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder, suggestions }: { value: string; onChange: (v: string) => void; placeholder?: string; suggestions?: { path: string }[] }) => (
    <input aria-label={placeholder} value={value} onChange={(e) => onChange(e.target.value)} data-suggestions={(suggestions ?? []).map((s) => s.path).join(",")} />
  ),
}));

/**
 * Stateful harness so multi-step edits round-trip through the controlled config + inputs, like the
 * real editor. flow.map's `items` collection lives in INPUTS; maxParallelism/errorHandling/resultKey
 * live in CONFIG (the MapConfig shape).
 */
function Harness({ config: initConfig = {}, inputs: initInputs = {} }: { config?: Record<string, unknown>; inputs?: Record<string, unknown> }) {
  const [config, setConfig] = useState(initConfig);
  const [inputs, setInputs] = useState(initInputs);
  return <MapEditor config={config} inputs={inputs} onConfigChange={setConfig} onInputsChange={setInputs} suggestions={[]} />;
}

const section = (title: string) => screen.getByText(title).closest("section") as HTMLElement;

describe("MapEditor", () => {
  it("reflects a saved config + inputs (items / maxParallelism / errorHandling / resultKey)", () => {
    render(
      <Harness
        inputs={{ items: "{{nodes.planner.outputs.json.subtasks}}" }}
        config={{ maxParallelism: 4, errorHandling: "continue", resultKey: "answers" }}
      />,
    );

    // items → the collection picker (mocked input)
    expect((within(section("For each item in")).getByRole("textbox") as HTMLInputElement).value).toBe("{{nodes.planner.outputs.json.subtasks}}");
    // maxParallelism → the branch-parallelism number
    expect((within(section("Branch parallelism")).getByRole("spinbutton") as HTMLInputElement).value).toBe("4");
    // errorHandling → the selector
    expect((within(section("Error handling")).getByRole("combobox") as HTMLSelectElement).value).toBe("continue");
    // resultKey → the text input
    expect((within(section("Result key")).getByRole("textbox") as HTMLInputElement).value).toBe("answers");
  });

  it("defaults: empty items, inherit parallelism, terminate, blank result key (placeholder 'results')", () => {
    render(<Harness />);

    expect((within(section("For each item in")).getByRole("textbox") as HTMLInputElement).value).toBe("");
    expect((within(section("Branch parallelism")).getByRole("spinbutton") as HTMLInputElement).value).toBe(""); // inherit
    expect((within(section("Error handling")).getByRole("combobox") as HTMLSelectElement).value).toBe("terminate");

    const resultKey = within(section("Result key")).getByRole("textbox") as HTMLInputElement;
    expect(resultKey.value).toBe("");
    expect(resultKey.placeholder).toBe("results");
  });

  it("patches the items collection ref into inputs", () => {
    const onInputsChange = vi.fn();
    render(<MapEditor config={{}} inputs={{}} onConfigChange={() => {}} onInputsChange={onInputsChange} suggestions={[]} />);

    fireEvent.change(within(section("For each item in")).getByRole("textbox"), { target: { value: "{{nodes.p.outputs.json.items}}" } });
    expect(onInputsChange).toHaveBeenLastCalledWith({ items: "{{nodes.p.outputs.json.items}}" });
  });

  it("offers only list-typed (or untyped) outputs in the 'for each' picker, hiding scalars", () => {
    const suggestions: ScopeSuggestion[] = [
      { path: "nodes.p.outputs.items", label: "P → items", category: "node", type: "array" },
      { path: "nodes.p.outputs.status", label: "P → status", category: "node", type: "string" },
      { path: "nodes.p.outputs.blob", label: "P → blob", category: "node" },   // untyped → kept (may be a list)
    ];
    render(<MapEditor config={{}} inputs={{}} onConfigChange={() => {}} onInputsChange={() => {}} suggestions={suggestions} />);

    const picker = within(section("For each item in")).getByRole("textbox");
    const offered = (picker.getAttribute("data-suggestions") ?? "").split(",").filter(Boolean);
    expect(offered).toContain("nodes.p.outputs.items");     // array → offered
    expect(offered).toContain("nodes.p.outputs.blob");      // untyped → offered
    expect(offered).not.toContain("nodes.p.outputs.status"); // scalar → hidden (you can't map over a string)
  });

  it("branch parallelism is empty by default (inherit), accepts a value, and clamps to [1, 64]", () => {
    const onConfigChange = vi.fn();
    const { rerender } = render(<MapEditor config={{}} inputs={{}} onConfigChange={onConfigChange} onInputsChange={() => {}} suggestions={[]} />);

    const num = () => within(section("Branch parallelism")).getByRole("spinbutton") as HTMLInputElement;
    expect(num().value).toBe("");

    // A value is clamped down to the ceiling and written to config.maxParallelism.
    fireEvent.change(num(), { target: { value: "999" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ maxParallelism: 64 });

    // Clearing the field removes the key entirely (back to inherit) rather than writing 0/NaN.
    rerender(<MapEditor config={{ maxParallelism: 2 }} inputs={{}} onConfigChange={onConfigChange} onInputsChange={() => {}} suggestions={[]} />);
    expect(num().value).toBe("2");
    fireEvent.change(num(), { target: { value: "" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({});
  });

  it("switches error handling from terminate to continue, writing config.errorHandling", () => {
    const onConfigChange = vi.fn();
    render(<MapEditor config={{}} inputs={{}} onConfigChange={onConfigChange} onInputsChange={() => {}} suggestions={[]} />);

    fireEvent.change(within(section("Error handling")).getByRole("combobox"), { target: { value: "continue" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ errorHandling: "continue" });
  });

  it("patches a result key into config, and clearing it removes the key (back to default 'results')", () => {
    const onConfigChange = vi.fn();
    const { rerender } = render(<MapEditor config={{}} inputs={{}} onConfigChange={onConfigChange} onInputsChange={() => {}} suggestions={[]} />);

    fireEvent.change(within(section("Result key")).getByRole("textbox"), { target: { value: "answers" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ resultKey: "answers" });

    rerender(<MapEditor config={{ resultKey: "answers" }} inputs={{}} onConfigChange={onConfigChange} onInputsChange={() => {}} suggestions={[]} />);
    fireEvent.change(within(section("Result key")).getByRole("textbox"), { target: { value: "" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({});
  });

  it("flags a reserved result key inline (the value still patches — the save-time validator is the hard gate)", () => {
    // "count" collides with the reducer's count output; the field shows an inline error and is marked invalid.
    render(<Harness config={{ resultKey: "count" }} />);

    const input = within(section("Result key")).getByRole("textbox") as HTMLInputElement;
    expect(input.getAttribute("aria-invalid")).toBe("true");

    const alert = within(section("Result key")).getByRole("alert");
    expect(alert.textContent).toContain("reserved");
  });

  it("flags a non-identifier result key inline", () => {
    render(<Harness config={{ resultKey: "my key" }} />);

    expect((within(section("Result key")).getByRole("textbox") as HTMLInputElement).getAttribute("aria-invalid")).toBe("true");
    expect(within(section("Result key")).getByRole("alert").textContent).toContain("can't be referenced");
  });

  it("accepts a valid result key with no inline error", () => {
    render(<Harness config={{ resultKey: "answers" }} />);

    const input = within(section("Result key")).getByRole("textbox") as HTMLInputElement;
    expect(input.getAttribute("aria-invalid")).toBe("false");
    expect(within(section("Result key")).queryByRole("alert")).toBeNull();
  });

  it("resultKeyError mirrors the backend reserved-set + identifier rule (and the blank default)", () => {
    expect(resultKeyError("")).toBeNull();          // blank ⇒ engine default "results"
    expect(resultKeyError("results")).toBeNull();   // the default itself
    expect(resultKeyError("answers")).toBeNull();
    expect(resultKeyError("_carry")).toBeNull();
    expect(resultKeyError("count")).toContain("reserved");
    expect(resultKeyError("failed")).toContain("reserved");
    expect(resultKeyError("1x")).toContain("can't be referenced");
    expect(resultKeyError("a-b")).toContain("can't be referenced");
    expect(resultKeyError("a.b")).toContain("can't be referenced");
  });
});
