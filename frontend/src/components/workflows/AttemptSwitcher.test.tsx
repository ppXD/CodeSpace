import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { RunAttempt } from "@/api/workflows";

import { AttemptSwitcher } from "./AttemptSwitcher";

const attempt = (n: number, runId: string, status: RunAttempt["status"], isLatest = false): RunAttempt => ({
  runId, attemptNumber: n, status, sourceType: n === 1 ? "manual" : "replay", createdDate: `2026-06-2${n}T00:00:00Z`, isLatest,
});

describe("AttemptSwitcher", () => {
  const ladder = [attempt(1, "r1", "Failure"), attempt(2, "r2", "Failure"), attempt(3, "r3", "Success", true)];

  it("renders a pill per attempt, flags the latest, and marks the selected one", () => {
    render(<AttemptSwitcher attempts={ladder} selectedRunId="r3" onSelect={() => {}} />);

    const pills = screen.getAllByRole("tab");
    expect(pills.map((p) => p.textContent)).toEqual(["Attempt 1", "Attempt 2", "Attempt 3latest"]);
    expect(pills[2].getAttribute("aria-selected")).toBe("true");      // r3 is selected
    expect(pills[0].getAttribute("aria-selected")).toBe("false");
  });

  it("selects the picked attempt's run id", () => {
    const onSelect = vi.fn();
    render(<AttemptSwitcher attempts={ladder} selectedRunId="r3" onSelect={onSelect} />);

    fireEvent.click(screen.getByRole("tab", { name: /attempt 1/i }));
    expect(onSelect).toHaveBeenCalledWith("r1");
  });

  it("renders nothing for a never-rerun run (a single attempt has nothing to switch)", () => {
    const { container } = render(<AttemptSwitcher attempts={[attempt(1, "solo", "Success", true)]} selectedRunId="solo" onSelect={() => {}} />);
    expect(container.firstChild).toBeNull();
  });
});
