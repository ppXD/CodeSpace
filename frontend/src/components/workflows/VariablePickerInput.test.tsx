import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { VariablePickerInput } from "./VariablePickerInput";

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
});
