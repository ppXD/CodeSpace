import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { RunLiveContext, type RunLiveStore } from "@/hooks/use-run-live";
import type { NodeLiveSignals, RunLiveState } from "@/lib/runLiveFold";

import type { WorkflowNodeData } from "../WorkflowNode";
import { digestTokens, TokenStreamFooter, type TokenDigest } from "./TokenStreamFooter";

// Freeze the shared clock so the buffered elapsed is deterministic; keep the real formatElapsed.
const FIXED_NOW = 2_000_000;
vi.mock("@/hooks/use-now-tick", async (importActual) => {
  const actual = await importActual<typeof import("@/hooks/use-now-tick")>();
  return { ...actual, useNowTick: () => FIXED_NOW };
});

/** Build a settled run row carrying the given outputs (+ optional start) — the fields the footer reads. */
function rowWith(outputs: unknown, startedAt: string | null = null): WorkflowRunNodeSummary[] {
  return [{ nodeId: "n1", iterationKey: "", status: "Success", inputs: {}, outputs, error: null, startedAt, completedAt: null }];
}

/** Render a digest label to plain text so the generic formatter can be asserted without DOM plumbing. */
function labelText(digest: TokenDigest | null): string {
  if (!digest) return "";
  return render(<>{digest.label}</>).container.textContent ?? "";
}

describe("digestTokens — generic terminal token digest", () => {
  it("in → out tok · cost (success)", () => {
    const digest = digestTokens(rowWith({ inputTokens: 1200, outputTokens: 350, costUsd: 0.0234, finishReason: "stop", json: null }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("1,200→350 tok · $0.02");
  });

  it("finishReason 'length' → warn tone + truncated", () => {
    const digest = digestTokens(rowWith({ inputTokens: 500, outputTokens: 2048, finishReason: "length" }));
    expect(digest?.tone).toBe("warn");
    expect(labelText(digest)).toBe("500→2,048 tok · truncated");
  });

  it("a json object output → json ✓ marker", () => {
    const digest = digestTokens(rowWith({ inputTokens: 80, outputTokens: 40, json: { items: [1, 2] } }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toContain("json ✓");
    expect(labelText(digest)).toContain("80→40 tok");
  });

  it("a positive sub-cent cost reads <$0.01, never a bare $0.00", () => {
    expect(labelText(digestTokens(rowWith({ inputTokens: 10, outputTokens: 5, costUsd: 0.0004 })))).toBe("10→5 tok · <$0.01");
  });

  it("missing token counts → null (falls back to the plain status·duration bar)", () => {
    expect(digestTokens(rowWith({ model: "claude", finishReason: "stop", json: { a: 1 } }))).toBeNull();
  });

  it("no keyed outputs → null", () => {
    expect(digestTokens(rowWith(null))).toBeNull();
    expect(digestTokens(rowWith("not-an-object"))).toBeNull();
    expect(digestTokens([])).toBeNull();
  });
});

/** A minimal run-live store exposing one node's signals — exercises the REAL useNodeLiveContext path via context. */
function fakeLiveStore(nodeId: string, signals: NodeLiveSignals): RunLiveStore {
  const state: RunLiveState = { byNode: new Map([[nodeId, signals]]), lastSeq: 0, terminal: false };
  return { getState: () => state, subscribe: () => () => {} };
}

/** A minimal AI node blob — the footer reads only nodeId. */
function nodeData(overrides: Partial<WorkflowNodeData> = {}): WorkflowNodeData {
  return { nodeId: "n1", typeKey: "llm.complete", displayName: "LLM completion", iconKey: "sparkles", kind: "Regular", category: "AI", label: null, ...overrides };
}

function renderFooter(status: NodeStatus, rows: WorkflowRunNodeSummary[], store?: RunLiveStore) {
  const data = nodeData();
  const footer = <TokenStreamFooter data={data} status={status} rows={rows} title={data.displayName} />;
  return render(store ? <RunLiveContext.Provider value={store}>{footer}</RunLiveContext.Provider> : footer);
}

describe("TokenStreamFooter — component", () => {
  it("Running + streaming renders the ≈token estimate, a caret, and 3 shimmer lines", () => {
    const store = fakeLiveStore("n1", { stream: { chars: 2000, deltas: 8, streaming: true }, lastEventSeq: 1 });
    const { container } = renderFooter("Running", [], store);

    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("Generating");
    expect(container.querySelector(".wf-rf-tok-count")?.textContent).toBe("≈500 tok");   // 2000 chars ÷ 4
    expect(container.querySelector(".wf-rf-tok-caret")).not.toBeNull();
    expect(container.querySelectorAll(".wf-rf-tok-line")).toHaveLength(3);
  });

  it("Running + buffered (no stream) renders a sparkle + elapsed and NO shimmer", () => {
    const store = fakeLiveStore("n1", { lastEventSeq: 1 });   // no stream signal → buffered
    const startedAt = new Date(FIXED_NOW - 65_000).toISOString();
    const { container } = renderFooter("Running", rowWith(null, startedAt), store);

    expect(container.querySelector(".wf-rf-tok-spark")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-dur")?.textContent).toBe("1:05");
    expect(container.querySelectorAll(".wf-rf-tok-line")).toHaveLength(0);
    expect(container.querySelector(".wf-rf-tok-caret")).toBeNull();
  });

  it("terminal Success stamps the token digest into the reused receipt bar", () => {
    const rows = rowWith({ inputTokens: 1200, outputTokens: 350, costUsd: 0.0234 });
    const { container } = renderFooter("Success", rows);

    expect(container.querySelector(".wf-rf-result")?.getAttribute("data-status")).toBe("success");
    expect(container.querySelector(".wf-rf-digest")?.textContent).toContain("1,200→350 tok");
    expect(container.querySelector(".wf-rf-digest")?.getAttribute("data-tone")).toBe("success");
  });

  it("a null-store Running renders degraded (buffered spinner) without throwing", () => {
    const { container } = renderFooter("Running", []);

    expect(container.querySelector(".wf-rf-status-spin")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("Generating");
    expect(container.querySelector(".wf-rf-tok-line")).toBeNull();
    expect(container.querySelector(".wf-rf-result-dur")).toBeNull();
  });
});
