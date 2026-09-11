import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/api/artifacts", () => ({ downloadArtifact: vi.fn() }));

import type { DeliverablesBlock, DeliveryBlock, RoomAgentLogStatus, RoomArtifactProducer, RoomConfinementPosture } from "@/api/sessions";
import { PrCard, ProducedFilesCard } from "./SessionRoomView";

const producer = (overrides: Partial<RoomArtifactProducer> = {}): RoomArtifactProducer => ({
  agentRunId: "r1",
  status: "Succeeded",
  logs: null,
  confinement: "ConfinedNetworkSevered",
  costUsd: 0.42,
  ...overrides,
});

const fileBlock = (made: RoomArtifactProducer | null): DeliverablesBlock => ({
  id: "turn-1:deliverables",
  seq: 4,
  type: "deliverables",
  title: "Produced 1 file",
  files: [{ path: "report.md", kind: "Document", sizeBytes: 1024, contentType: "text/markdown", artifactId: "a1", agentRunId: "r1", availability: "Reachable", producer: made }],
});

const delivery = (overrides: Partial<DeliveryBlock> = {}): DeliveryBlock => ({
  id: "delivery-api",
  seq: 1,
  type: "delivery",
  title: "Ship the repository",
  repositoryAlias: "api",
  ...overrides,
});

describe("per-artifact producer truth (P21-8b)", () => {
  it("renders nothing extra for a file that carries no producer — the contract is additive", () => {
    // A manifest outlives its agent-run row, so an absent producer is a real absence. It must leave the row exactly
    // as it read before this slice, never a chip full of blanks.
    render(<ProducedFilesCard block={fileBlock(null)} />);

    expect(screen.getByRole("button", { name: "report.md" })).toBeInTheDocument();
    expect(screen.queryByText(/posture unknown|cost unknown/)).not.toBeInTheDocument();
  });

  it.each<[string, RoomArtifactProducer]>([
    ["Queued", producer({ status: "Queued", confinement: "Unknown", costUsd: null, logs: null })],
    ["Running", producer({ status: "Running", confinement: "Confined", costUsd: null, logs: "Finalizing" })],
    ["Failed", producer({ status: "Failed", confinement: "Unconfined", costUsd: 1.25, logs: "Incomplete" })],
    ["Cancelled", producer({ status: "Cancelled", confinement: "Unknown", costUsd: null, logs: "Stalled" })],
    ["Succeeded", producer({ status: "Succeeded", confinement: "ConfinedNetworkSevered", costUsd: 0.42, logs: "Verified" })],
  ])("states the producing agent's own execution word in every state: %s", (status, made) => {
    render(<ProducedFilesCard block={fileBlock(made)} />);

    expect(screen.getByText(new RegExp(status))).toBeInTheDocument();
  });

  it.each<[RoomConfinementPosture, string]>([
    ["Unknown", "posture unknown"],
    ["Unconfined", "unconfined"],
    ["Confined", "confined"],
    ["ConfinedNetworkSevered", "confined · egress severed"],
  ])("names the posture %s as its own word", (confinement, expected) => {
    render(<ProducedFilesCard block={fileBlock(producer({ confinement }))} />);

    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it("says posture unknown rather than showing a confined word for a producer nothing recorded", () => {
    // The mutation this catches at the render layer: treating Unknown as confined tells an operator the host
    // enforced an isolation nobody evidenced — on exactly the runs that most need the hedge.
    render(<ProducedFilesCard block={fileBlock(producer({ confinement: "Unknown" }))} />);

    expect(screen.getByText("posture unknown")).toBeInTheDocument();
    expect(screen.queryByText("confined")).not.toBeInTheDocument();
    expect(screen.queryByText("confined · egress severed")).not.toBeInTheDocument();
  });

  it("says cost unknown for an unpriceable producer rather than rendering it as free", () => {
    render(<ProducedFilesCard block={fileBlock(producer({ costUsd: null }))} />);

    expect(screen.getByText("cost unknown")).toBeInTheDocument();
    expect(screen.queryByText("$0")).not.toBeInTheDocument();
  });

  it("renders a priced producer's realized spend", () => {
    render(<ProducedFilesCard block={fileBlock(producer({ costUsd: 0.42 }))} />);

    expect(screen.getByText("$0.420")).toBeInTheDocument();
    expect(screen.queryByText("cost unknown")).not.toBeInTheDocument();
  });

  it.each<[RoomAgentLogStatus, string]>([
    ["Verified", "logs integrity verified"],
    ["Captured", "logs captured"],
    ["Finalizing", "logs finalizing"],
    ["Incomplete", "logs incomplete"],
    ["Stalled", "logs held; storage unavailable"],
  ])("distinguishes the producer's log health %s from every other", (logs, expected) => {
    render(<ProducedFilesCard block={fileBlock(producer({ logs }))} />);

    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it("leaves the log health unsaid for a producer that declared no stream", () => {
    render(<ProducedFilesCard block={fileBlock(producer({ logs: null }))} />);

    expect(screen.queryByText(/^logs /)).not.toBeInTheDocument();
  });

  it("names each repository's own producers on its own PR card", () => {
    const api = delivery({ id: "delivery-api", repositoryAlias: "api", producers: [producer({ agentRunId: "api-agent", status: "Succeeded" })] });
    const web = delivery({ id: "delivery-web", repositoryAlias: "web", producers: [producer({ agentRunId: "web-agent", status: "Failed", costUsd: null })] });

    render(<>{[api, web].map((d) => <PrCard key={d.id} delivery={d} />)}</>);

    expect(screen.getByText(/Succeeded/)).toBeInTheDocument();
    expect(screen.getByText(/Failed/)).toBeInTheDocument();
    expect(screen.getByText("cost unknown")).toBeInTheDocument();
  });

  it("renders every producer of a repository several agents delivered into", () => {
    const both = delivery({ producers: [producer({ agentRunId: "one", status: "Succeeded" }), producer({ agentRunId: "two", status: "Failed", costUsd: null })] });

    render(<PrCard delivery={both} />);

    expect(screen.getByText(/Succeeded/)).toBeInTheDocument();
    expect(screen.getByText(/Failed/)).toBeInTheDocument();
  });
});
