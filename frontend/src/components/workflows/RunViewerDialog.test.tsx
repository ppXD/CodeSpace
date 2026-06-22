import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RunViewerDialog } from "./RunViewerDialog";

// Stub the run detail — this suite tests the dialog's drill stack (open child → Back), not the run view.
// The stub exposes a button that fires onOpenRun, standing in for a sub-workflow node's "Open" affordance.
vi.mock("./RunDetailView", () => ({
  RunDetailView: ({ runId, onOpenRun }: { runId: string; onOpenRun?: (id: string) => void }) => (
    <div>
      <span data-testid="rdv-runid">{runId}</span>
      <button onClick={() => onOpenRun?.("child000-bbbb")}>open-child</button>
    </div>
  ),
}));

function title(c: HTMLElement) { return c.querySelector(".mdl-title")?.textContent; }

describe("RunViewerDialog drill stack", () => {
  it("opens a child run in place and Back returns to the parent", () => {
    const { container, getByText, getByTestId } = render(<RunViewerDialog runId="parent00-aaaa" onClose={vi.fn()} />);

    expect(getByTestId("rdv-runid").textContent).toBe("parent00-aaaa");
    expect(title(container)).toBe("Run parent00");
    expect(container.querySelector(".mdl-back")).toBeNull();          // no Back at the top level

    fireEvent.click(getByText("open-child"));
    expect(getByTestId("rdv-runid").textContent).toBe("child000-bbbb");
    expect(title(container)).toBe("Run child000");
    expect(container.querySelector(".mdl-back")).not.toBeNull();      // Back appears once drilled

    fireEvent.click(container.querySelector(".mdl-back")!);
    expect(getByTestId("rdv-runid").textContent).toBe("parent00-aaaa");
    expect(container.querySelector(".mdl-back")).toBeNull();
  });

  it("resets the stack when the host opens a different top-level run", () => {
    const { container, getByText, getByTestId, rerender } = render(<RunViewerDialog runId="parent00-aaaa" onClose={vi.fn()} />);

    fireEvent.click(getByText("open-child"));
    expect(container.querySelector(".mdl-back")).not.toBeNull();

    rerender(<RunViewerDialog runId="other999-cccc" onClose={vi.fn()} />);
    expect(getByTestId("rdv-runid").textContent).toBe("other999-cccc");
    expect(container.querySelector(".mdl-back")).toBeNull();          // drill reset on new run
  });
});
