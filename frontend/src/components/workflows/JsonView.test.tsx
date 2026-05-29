import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { JsonView } from "./JsonView";

/**
 * JsonView is the collapsible JSON tree behind RunDetailView's normalized payload, run outputs
 * and per-node inputs/outputs. These tests pin the behaviours a reader depends on:
 *   1. everything renders (and is type-coloured) on mount — no data hidden by default;
 *   2. any object/array subtree folds + unfolds on click, and shows an entry count when folded;
 *   3. empty containers render inline with no toggle;
 *   4. arrays count "items", objects count "keys", with correct singular/plural;
 *   5. the toggle is keyboard-operable.
 */

/** Find the disclosure row whose key label is `"<key>"`. */
function toggleFor(container: HTMLElement, key: string): HTMLElement {
  const row = [...container.querySelectorAll<HTMLElement>(".wf-jsonv-toggle")].find(
    (t) => t.querySelector(".wf-jsonv-key")?.textContent === `"${key}"`,
  );
  if (!row) throw new Error(`no toggle for key "${key}"`);
  return row;
}

describe("JsonView", () => {
  it("renders every key and type-coloured scalar on mount", () => {
    const { container } = render(
      <JsonView data={{ name: "alpha", count: 3, ok: true, missing: null }} />,
    );

    const keys = [...container.querySelectorAll(".wf-jsonv-key")].map((k) => k.textContent);
    expect(keys).toEqual(['"name"', '"count"', '"ok"', '"missing"']);

    expect(container.querySelector(".wf-jsonv-str")?.textContent).toBe('"alpha"');
    expect(container.querySelector(".wf-jsonv-num")?.textContent).toBe("3");
    expect(container.querySelector(".wf-jsonv-bool")?.textContent).toBe("true");
    expect(container.querySelector(".wf-jsonv-null")?.textContent).toBe("null");
  });

  it("folds a subtree on click (hiding its children + showing a count), then unfolds", () => {
    const { container } = render(<JsonView data={{ repo: { id: "r1", url: "x" } }} />);

    expect(container.textContent).toContain('"id"');

    fireEvent.click(toggleFor(container, "repo"));

    expect(container.textContent).not.toContain('"id"');
    expect(container.querySelector(".wf-jsonv-count")?.textContent).toContain("2 keys");

    fireEvent.click(toggleFor(container, "repo"));

    expect(container.textContent).toContain('"id"');
    expect(container.querySelector(".wf-jsonv-count")).toBeNull();
  });

  it("renders empty objects and arrays inline with no toggle", () => {
    const { container } = render(<JsonView data={{ obj: {}, arr: [] }} />);

    // Only the root object is collapsible; the two empty children are not.
    expect(container.querySelectorAll(".wf-jsonv-toggle")).toHaveLength(1);
    expect(container.textContent).toContain("{}");
    expect(container.textContent).toContain("[]");
  });

  it("counts array entries as items and object entries as keys", () => {
    const { container } = render(<JsonView data={{ tags: ["a", "b", "c"] }} />);

    fireEvent.click(toggleFor(container, "tags"));

    expect(container.querySelector(".wf-jsonv-count")?.textContent).toContain("3 items");
  });

  it("uses singular unit labels for single-entry containers", () => {
    const { container } = render(<JsonView data={{ one: [42] }} />);

    fireEvent.click(toggleFor(container, "one"));

    expect(container.querySelector(".wf-jsonv-count")?.textContent).toContain("1 item");
  });

  it("toggles via the keyboard (Enter)", () => {
    const { container } = render(<JsonView data={{ repo: { id: "r1" } }} />);

    fireEvent.keyDown(toggleFor(container, "repo"), { key: "Enter" });

    expect(container.textContent).not.toContain('"id"');
  });
});
