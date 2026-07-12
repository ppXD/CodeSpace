import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { LogicIfEditor } from "./LogicIfEditor";
import type { ScopeSuggestion } from "./scope-introspection";

// The {{}} picker is tested elsewhere; a plain input keeps the focus on the If editor's parse/serialize wiring.
vi.mock("./VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) => (
    <input aria-label={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

const render_ = (condition: string, suggestions: ScopeSuggestion[] = []) => {
  const onConfigChange = vi.fn();
  render(<LogicIfEditor config={{ condition }} onConfigChange={onConfigChange} suggestions={suggestions} />);
  return onConfigChange;
};

const valueInput = () => screen.getByLabelText("pick a value — type @") as HTMLInputElement;
const compareInput = () => screen.queryByLabelText("a value or some text") as HTMLInputElement | null;
const opSelect = () => screen.getByRole("combobox") as HTMLSelectElement;

describe("LogicIfEditor", () => {
  it("opens an existing condition into value / operator / compare-to", () => {
    render_('{{trigger.state}} == "open"');

    expect(valueInput().value).toBe("{{trigger.state}}");
    expect(opSelect().value).toBe("==");
    expect(compareInput()!.value).toBe("open");                 // the quoted literal shows unquoted
  });

  it("rewrites the condition string when the operator changes", () => {
    const onConfigChange = render_('{{s}} == "open"');
    fireEvent.change(opSelect(), { target: { value: ">=" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ condition: '{{s}} >= "open"' });
  });

  it("re-quotes a bare compare-to value on save; a ref stays unquoted", () => {
    const onConfigChange = render_('{{s}} == "x"');
    fireEvent.change(compareInput()!, { target: { value: "open" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ condition: '{{s}} == "open"' });

    fireEvent.change(compareInput()!, { target: { value: "{{other}}" } });
    expect(onConfigChange).toHaveBeenLastCalledWith({ condition: "{{s}} == {{other}}" });
  });

  it("hides the compare-to field for a unary operator", () => {
    render_("{{nodes.fetch.outputs.files}} is_not_empty");
    expect(opSelect().value).toBe("is_not_empty");
    expect(compareInput()).toBeNull();
  });

  it("narrows the operator list to the left value's type (a Yes/No hides string ops)", () => {
    render_("{{flag}}", [{ path: "flag", label: "flag", category: "node", type: "boolean" }]);
    const opValues = Array.from(opSelect().options).map((o) => o.value);
    expect(opValues).toEqual(["==", "!=", "truthy"]);            // no contains / startsWith for a boolean
  });

  it("offers a raw-expression escape hatch that shows the full string", () => {
    render_('{{s}} contains "urgent"');
    fireEvent.click(screen.getByText("Edit as an expression instead"));
    expect(screen.getByLabelText(/== "open"/)).toBeTruthy();     // the raw editor (its placeholder), guided rows gone
    expect(screen.queryByRole("combobox")).toBeNull();
  });
});
