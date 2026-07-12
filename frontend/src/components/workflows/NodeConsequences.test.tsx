import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { NodeConsequences } from "./NodeConsequences";

describe("NodeConsequences", () => {
  it("renders nothing for a plain node (no side effects)", () => {
    const { container } = render(<NodeConsequences source={{}} />);
    expect(container.firstChild).toBeNull();
  });

  it("spells out each consequence the manifest flags", () => {
    render(<NodeConsequences source={{ isSideEffecting: true, canSuspend: true, alwaysRequiresApproval: true }} />);

    expect(screen.getByText(/approval before it acts/i)).toBeTruthy();
    expect(screen.getByText(/writes to external systems/i)).toBeTruthy();
    expect(screen.getByText(/pause the run and wait/i)).toBeTruthy();
  });

  it("shows only the flags that are set", () => {
    render(<NodeConsequences source={{ isSideEffecting: true }} />);

    expect(screen.getByText(/writes to external systems/i)).toBeTruthy();
    expect(screen.queryByText(/approval before it acts/i)).toBeNull();
    expect(screen.queryByText(/pause the run and wait/i)).toBeNull();
  });
});
