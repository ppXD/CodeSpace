import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { NodeBadges } from "./NodeBadges";

describe("NodeBadges", () => {
  it("renders nothing for a plain node so the canvas + palette stay quiet", () => {
    const { container } = render(<NodeBadges source={{}} />);
    expect(container.querySelectorAll(".wf-badge")).toHaveLength(0);
  });

  it("carries each label as title + aria-label so a palette dot (label visually collapsed) stays identifiable", () => {
    const { container } = render(<NodeBadges source={{ alwaysRequiresApproval: true, isSideEffecting: true, canSuspend: true }} />);
    const badges = [...container.querySelectorAll<HTMLElement>(".wf-badge")];

    // most-consequential first — this fixed DOM order is what lets position encode kind among the dots
    expect(badges.map((b) => b.dataset.badge)).toEqual(["approval", "write", "wait"]);

    for (const b of badges) {
      expect(b.getAttribute("title")).toBe(b.textContent);
      expect(b.getAttribute("aria-label")).toBe(b.textContent);
    }
  });
});
