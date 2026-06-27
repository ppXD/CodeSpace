import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunAttempt } from "@/api/workflows";

import { NodeRerunBadge } from "./NodeRerunBadge";
import { RerunProvenanceContext, type RerunProvenance } from "./rerunProvenanceContext";
import { rerunsByNode } from "./runRerunProvenance";

const attempt = (n: number, rerunFromNodeId: string | null, status: RunAttempt["status"] = "Success"): RunAttempt => ({
  runId: `r${n}`, attemptNumber: n, status, sourceType: n === 1 ? "manual" : "rerun", rerunFromNodeId, createdDate: `2026-06-2${n}T00:00:00Z`, isLatest: false,
});

function renderBadge(nodeId: string, attempts: RunAttempt[]) {
  const value: RerunProvenance = { attempts, rerunsByNode: rerunsByNode(attempts) };
  return render(
    <RerunProvenanceContext.Provider value={value}>
      <NodeRerunBadge nodeId={nodeId} />
    </RerunProvenanceContext.Provider>,
  );
}

describe("NodeRerunBadge", () => {
  const ladder = [attempt(1, null), attempt(2, "agent", "Failure"), attempt(3, "agent", "Success")];

  it("shows a 'reran ·N' badge for a re-run node and opens its history", () => {
    renderBadge("agent", ladder);

    const badge = screen.getByRole("button", { name: /reran ·2/i });
    expect(badge.getAttribute("title")).toMatch(/re-ran 2 times/i);

    fireEvent.click(badge);
    expect(screen.getByText("agent · rerun history")).toBeInTheDocument();
    const rows = screen.getAllByText(/^Attempt \d$/);
    expect(rows.map((r) => r.textContent)).toEqual(["Attempt 2", "Attempt 3"]);   // the two reruns, oldest first
  });

  it("renders nothing for a node that was never re-run", () => {
    const { container } = renderBadge("never-touched", ladder);
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing with no provenance context", () => {
    const { container } = render(<NodeRerunBadge nodeId="agent" />);
    expect(container.firstChild).toBeNull();
  });
});
