import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { VariablePickerInput } from "./VariablePickerInput";
import type { ScopeSuggestion } from "./scope-introspection";

/**
 * Regression guard for the "value vanishes on reload" bug: a field opened with an existing
 * value (e.g. reopening a saved workflow whose node input is `{{input.repo}}`) MUST render that
 * value into the contenteditable on mount. Before the fix the hydration ref was initialised to
 * `value`, so the mount-time effect short-circuited and the editor rendered empty — then a blur
 * flushed the empty DOM back as "", silently dropping the saved value.
 */
describe("VariablePickerInput initial hydration", () => {
  it("renders an existing {{ref}} value as a chip on mount", () => {
    const { container } = render(
      <VariablePickerInput value="{{input.repo}}" onChange={() => {}} suggestions={[]} />,
    );
    const chip = container.querySelector(".wf-picker-chip");
    expect(chip).not.toBeNull();
    expect(chip?.getAttribute("data-path")).toBe("input.repo");
  });

  it("renders mixed literal text + ref on mount", () => {
    const { container } = render(
      <VariablePickerInput value="hi {{input.name}} there" onChange={() => {}} suggestions={[]} />,
    );
    expect(container.querySelector(".wf-picker-chip")?.getAttribute("data-path")).toBe("input.name");
    expect(container.textContent).toContain("hi");
    expect(container.textContent).toContain("there");
  });

  it("renders no chip for an empty value", () => {
    const { container } = render(
      <VariablePickerInput value="" onChange={() => {}} suggestions={[]} />,
    );
    expect(container.querySelector(".wf-picker-chip")).toBeNull();
  });

  it("re-hydrates an INDEXED ref (arr[0].field) into a chip on mount", () => {
    // The picker emits `pullRequests[0].url` refs; reopening a saved workflow must render them as chips, not
    // raw text (regression guard for the rehydration regex that once rejected `[` `]`).
    const { container } = render(
      <VariablePickerInput value="{{nodes.list.outputs.pullRequests[0].url}}" onChange={() => {}} suggestions={[]} />,
    );
    expect(container.querySelector(".wf-picker-chip")?.getAttribute("data-path")).toBe("nodes.list.outputs.pullRequests[0].url");
  });
});

const treeSuggestions: ScopeSuggestion[] = [
  { path: "nodes.plan.outputs.items", label: "Create plan → items", category: "node", type: "array", description: "nodes.plan.outputs.items" },
  { path: "nodes.plan.outputs.items[0].instruction", label: "Create plan → items[0].instruction", category: "node", type: "string", description: "nodes.plan.outputs.items[0].instruction" },
];

/**
 * The picker renders suggestions as a COLLAPSIBLE TREE (grouped by source node, fields drilling underneath).
 * These exercise the interaction the suggestionTree unit tests can't: open → expand → select inserts the ref.
 */
describe("VariablePickerInput tree interaction", () => {
  const openPicker = () => fireEvent.click(screen.getByTitle(/Insert a variable/));

  it("shows a collapsed source branch and reveals fields on expand", () => {
    render(<VariablePickerInput value="" onChange={() => {}} suggestions={treeSuggestions} />);
    openPicker();

    expect(screen.getByText("Create plan")).toBeTruthy();   // source branch
    expect(screen.queryByText("Items")).toBeNull();          // collapsed — fields hidden

    fireEvent.mouseDown(screen.getByText("Create plan"));    // expand the source
    expect(screen.getByText("Items")).toBeTruthy();

    const itemsRow = screen.getByText("Items").closest(".wf-picker-item")!;
    fireEvent.mouseDown(itemsRow.querySelector(".wf-picker-twist")!);   // expand the array via its twist
    expect(screen.getByText("First item")).toBeTruthy();     // the [0] drill level
  });

  it("routes a click on a selectable field to insert (closes the picker); a branch only expands", () => {
    // A whole-array row is BOTH selectable and expandable: clicking its LABEL selects (and closes the picker);
    // clicking a non-selectable branch label only expands (picker stays open, writes nothing).
    const onChange = vi.fn();
    render(<VariablePickerInput value="" onChange={onChange} suggestions={treeSuggestions} />);
    openPicker();

    fireEvent.mouseDown(screen.getByText("Create plan"));    // branch → expands, picker stays open
    expect(screen.getByText("Items")).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();                 // expanding a branch writes nothing

    fireEvent.mouseDown(screen.getByText("Items"));          // selectable → insert → picker closes
    expect(screen.queryByText("Create plan")).toBeNull();
  });

  it("serializes a chip back to its exact {{ref}} value (the emission a regression could silently drop)", () => {
    // jsdom can't drive the picker-click chip insert, but the load-bearing chip→value path (nodeToValue must
    // wrap the chip's data-path in {{ }}) IS testable via a flush: append text to a hydrated chip, blur, and
    // assert onChange carries the ref verbatim. A regression dropping the braces or emitting chip text fails here.
    const onChange = vi.fn();
    const { container } = render(
      <VariablePickerInput value="{{nodes.plan.outputs.items}}" onChange={onChange} suggestions={[]} />,
    );
    const editor = container.querySelector(".wf-picker-editor") as HTMLElement;
    editor.appendChild(document.createTextNode(" x"));
    fireEvent.blur(editor);
    expect(onChange).toHaveBeenLastCalledWith("{{nodes.plan.outputs.items}} x");
  });
});
