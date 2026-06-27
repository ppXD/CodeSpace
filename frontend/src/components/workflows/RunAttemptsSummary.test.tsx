import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunAttempt } from "@/api/workflows";

import { RunAttemptsSummary } from "./RunAttemptsSummary";

const attempt = (n: number, rerunFromNodeId: string | null): RunAttempt => ({
  runId: `r${n}`, attemptNumber: n, status: "Success", sourceType: n === 1 ? "manual" : "rerun", rerunFromNodeId, createdDate: `2026-06-2${n}T00:00:00Z`, isLatest: n === 3,
});

describe("RunAttemptsSummary", () => {
  it("shows 'N attempts' and lists each attempt with the node it re-ran", () => {
    render(<RunAttemptsSummary attempts={[attempt(1, null), attempt(2, "agent"), attempt(3, "agent")]} />);

    const pill = screen.getByRole("button", { name: /3 attempts/i });
    fireEvent.click(pill);

    expect(screen.getByText("first run")).toBeInTheDocument();             // the original
    expect(screen.getAllByText("reran agent")).toHaveLength(2);            // the two reruns
  });

  it("renders nothing for a never-rerun run (a single attempt)", () => {
    const { container } = render(<RunAttemptsSummary attempts={[attempt(1, null)]} />);
    expect(container.firstChild).toBeNull();
  });
});
