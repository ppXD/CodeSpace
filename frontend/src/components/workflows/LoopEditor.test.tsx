import { useState } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { LoopEditor } from "./LoopEditor";

// The {{}} picker is exercised elsewhere; here a plain input keeps the focus on LoopEditor's logic.
vi.mock("./VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) => (
    <input aria-label={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

/** Stateful harness so multi-step edits round-trip through the controlled config, like the real editor. */
function Harness({ initial }: { initial: Record<string, unknown> }) {
  const [config, setConfig] = useState(initial);
  return <LoopEditor config={config} onConfigChange={setConfig} suggestions={[]} />;
}

const section = (title: string) => screen.getByText(title).closest("section") as HTMLElement;

describe("LoopEditor", () => {
  it("defaults max iterations to 10 and clamps out-of-range input", () => {
    render(<Harness initial={{}} />);

    const num = within(section("Max iterations")).getByRole("spinbutton") as HTMLInputElement;
    expect(num.value).toBe("10");

    fireEvent.change(num, { target: { value: "5000" } });
    expect((within(section("Max iterations")).getByRole("spinbutton") as HTMLInputElement).value).toBe("1000");

    fireEvent.change(within(section("Max iterations")).getByRole("spinbutton"), { target: { value: "0" } });
    expect((within(section("Max iterations")).getByRole("spinbutton") as HTMLInputElement).value).toBe("1");
  });

  it("adds a loop variable, defaulting to a Constant source", () => {
    render(<Harness initial={{}} />);

    fireEvent.click(within(section("Loop variables")).getByRole("button", { name: /add/i }));

    // A constant var exposes a plain initial-value input, not the variable picker.
    const vars = section("Loop variables");
    fireEvent.change(within(vars).getByPlaceholderText("name"), { target: { value: "acc" } });
    expect((within(vars).getByPlaceholderText("name") as HTMLInputElement).value).toBe("acc");
    expect(within(vars).getByPlaceholderText("Initial constant value")).toBeTruthy();
  });

  it("switches a variable to the Variable source, swapping the value field for the picker", () => {
    render(<Harness initial={{ loopVariables: [{ name: "x", type: "String", value: "" }] }} />);

    const vars = section("Loop variables");
    const sourceSel = within(vars).getAllByRole("combobox").find((s) => (s as HTMLSelectElement).value === "constant")!;
    fireEvent.change(sourceSel, { target: { value: "variable" } });

    // Now the picker (mocked input, placeholder "Initial value…") is shown instead of the constant box.
    expect(within(section("Loop variables")).getByLabelText("Initial value — pick a variable")).toBeTruthy();
    expect(within(section("Loop variables")).queryByPlaceholderText("Initial constant value")).toBeNull();
  });

  it("adds a termination condition; a unary operator hides the value box", () => {
    render(<Harness initial={{}} />);

    fireEvent.click(within(section("Termination condition")).getByRole("button", { name: /add/i }));

    const term = section("Termination condition");
    // Binary op (default contains) shows a value input.
    expect(within(term).getByPlaceholderText("value")).toBeTruthy();

    const opSel = within(term).getAllByRole("combobox").find((s) => (s as HTMLSelectElement).value === "contains")!;
    fireEvent.change(opSel, { target: { value: "is_empty" } });

    // Unary op → no value box.
    expect(within(section("Termination condition")).queryByPlaceholderText("value")).toBeNull();
  });

  it("defaults error handling to terminate and switches to continue", () => {
    render(<Harness initial={{}} />);

    const sel = within(section("Error handling")).getByRole("combobox") as HTMLSelectElement;
    expect(sel.value).toBe("terminate");

    fireEvent.change(sel, { target: { value: "continue" } });
    expect((within(section("Error handling")).getByRole("combobox") as HTMLSelectElement).value).toBe("continue");
  });
});
