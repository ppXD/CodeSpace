import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/api/artifacts", () => ({ downloadArtifact: vi.fn() }));

import type { DeliverablesBlock, DeliveryBlock, RoomArtifactVerification } from "@/api/sessions";
import { PrCard, ProducedFilesCard } from "./SessionRoomView";

const verification = (overrides: Partial<RoomArtifactVerification> = {}): RoomArtifactVerification => ({
  artifactOrRepositoryRef: "api",
  checkKind: "acceptance",
  ran: true,
  passed: true,
  detail: null,
  oracleProtection: "None",
  evidenceArtifactId: null,
  logsComplete: null,
  ...overrides,
});

const delivery = (overrides: Partial<DeliveryBlock> = {}): DeliveryBlock => ({
  id: "delivery-api",
  seq: 1,
  type: "delivery",
  title: "Ship the repository",
  repositoryAlias: "api",
  ...overrides,
});

describe("per-artifact / per-repository verification (P21)", () => {
  it("renders nothing extra for a delivery that carries no verifications — the contract is additive", () => {
    render(<PrCard delivery={delivery()} />);

    expect(screen.queryByText(/passed|failed|unrun|ungraded/)).not.toBeInTheDocument();
  });

  it("shows a passed check's word without a protection tag when nothing was flagged", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ passed: true })] })} />);

    expect(screen.getByText(/acceptance passed/)).toBeInTheDocument();
    expect(screen.queryByText("self-graded")).not.toBeInTheDocument();
    expect(screen.queryByText("unanchored")).not.toBeInTheDocument();
  });

  it("keeps two repositories' verdicts distinct — one passing never bleeds into a sibling's failure", () => {
    // The P21 invariant at the render layer: each PrCard only ever sees its OWN delivery's verifications.
    const api = delivery({ id: "delivery-api", repositoryAlias: "api", verifications: [verification({ passed: true })] });
    const web = delivery({ id: "delivery-web", repositoryAlias: "web", verifications: [verification({ artifactOrRepositoryRef: "web", passed: false, detail: "tests-failed-exit-1" })] });

    render(<>{[api, web].map((d) => <PrCard key={d.id} delivery={d} />)}</>);

    expect(screen.getByText(/acceptance passed/)).toBeInTheDocument();
    expect(screen.getByText(/acceptance failed/)).toBeInTheDocument();
  });

  it("says a check never ran rather than fabricating a pass", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ ran: false, passed: null })] })} />);

    expect(screen.getByText(/acceptance unrun/)).toBeInTheDocument();
  });

  it("tags a self-graded (subject) pass so it is never confused with a protected one", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ oracleProtection: "Subject" })] })} />);

    expect(screen.getByText("self-graded")).toBeInTheDocument();
  });

  it("tags an unanchored judge distinctly from a self-graded one", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ oracleProtection: "Unanchored" })] })} />);

    expect(screen.getByText("unanchored")).toBeInTheDocument();
  });

  it("notes an incomplete log WITHOUT hiding or flipping an otherwise-verified delivery", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ passed: true, logsComplete: false })] })} />);

    expect(screen.getByText(/acceptance passed/)).toBeInTheDocument();
    expect(screen.getByText("logs incomplete")).toBeInTheDocument();
  });

  it("carries the grader's detail as a tooltip on the check's own chip", () => {
    render(<PrCard delivery={delivery({ verifications: [verification({ passed: false, detail: "tests-failed-exit-1" })] })} />);

    expect(screen.getByText(/acceptance failed/)).toHaveAttribute("title", "tests-failed-exit-1");
  });

  it("attaches a file's own verification under that file's row, not the block as a whole", () => {
    const block: DeliverablesBlock = {
      id: "turn-1:deliverables",
      seq: 4,
      type: "deliverables",
      title: "Produced 1 file",
      files: [{
        path: "report.md", kind: "Document", sizeBytes: 1024, contentType: "text/markdown", artifactId: "a1", agentRunId: "r1", availability: "Reachable",
        verifications: [verification({ artifactOrRepositoryRef: "r1", passed: true })],
      }],
    };

    render(<ProducedFilesCard block={block} />);

    expect(screen.getByRole("button", { name: "report.md" })).toBeInTheDocument();
    expect(screen.getByText(/acceptance passed/)).toBeInTheDocument();
  });
});
