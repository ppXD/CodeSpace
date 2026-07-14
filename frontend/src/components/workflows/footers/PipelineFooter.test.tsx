import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { RunLiveContext, type RunLiveStore } from "@/hooks/use-run-live";
import type { NodeLiveSignals, RunLiveState } from "@/lib/runLiveFold";

import type { WorkflowNodeData } from "../WorkflowNode";
import { PipelineFooter, pipelineDigest, type PipelineDigest } from "./PipelineFooter";

// Freeze the shared clock so the live/row elapsed is deterministic; keep the real formatElapsed.
const FIXED_NOW = 1_000_000;
vi.mock("@/hooks/use-now-tick", async (importActual) => {
  const actual = await importActual<typeof import("@/hooks/use-now-tick")>();
  return { ...actual, useNowTick: () => FIXED_NOW };
});

/** Build a settled run row carrying the given outputs (+ optional start) — the fields the footer reads. */
function rowWith(outputs: unknown, startedAt: string | null = null): WorkflowRunNodeSummary[] {
  return [{ nodeId: "n1", iterationKey: "", status: "Success", inputs: {}, outputs, error: null, startedAt, completedAt: startedAt }];
}

/** Render a digest label to plain text so the pure formatter can be asserted without DOM plumbing. */
function labelText(digest: PipelineDigest | null): string {
  if (!digest) return "";
  return render(<>{digest.label}</>).container.textContent ?? "";
}

describe("pipelineDigest — git.integrate outcomes", () => {
  it("Clean → branch chip + N/N applied (success)", () => {
    const digest = pipelineDigest("git.integrate", rowWith({ status: "Clean", integratedBranch: "codespace/integration/run-7", appliedCount: 3, conflicts: [] }));
    expect(digest?.tone).toBe("success");

    const text = labelText(digest);
    expect(text).toContain("codespace/integration/run-7");
    expect(text).toContain("3/3 applied");
  });

  it("Conflicted → first conflict label + reason, AMBER (warn), not failure", () => {
    const digest = pipelineDigest("git.integrate", rowWith({
      status: "Conflicted",
      integratedBranch: null,
      appliedCount: 0,
      conflicts: [{ label: "fix-auth", disposition: "Conflicted", reason: "base SHA mismatch" }, { label: "other", disposition: "Unintegrable" }],
    }));
    expect(digest?.tone).toBe("warn");

    const text = labelText(digest);
    expect(text).toContain("fix-auth");
    expect(text).toContain("base SHA mismatch");
  });

  it("Empty → muted '無可整合'", () => {
    const digest = pipelineDigest("git.integrate", rowWith({ status: "Empty", appliedCount: 0, conflicts: [] }));
    expect(labelText(digest)).toContain("無可整合");
  });

  it("returns null for an unknown integration status", () => {
    expect(pipelineDigest("git.integrate", rowWith({ status: "Weird" }))).toBeNull();
  });
});

describe("pipelineDigest — agent.run_command exit codes", () => {
  it("exit 0 → green (success) with byte counts", () => {
    const digest = pipelineDigest("agent.run_command", rowWith({ exitCode: 0, status: "Success", stdoutBytes: 2048, stderrBytes: 0 }));
    expect(digest?.tone).toBe("success");

    const text = labelText(digest);
    expect(text).toContain("exit 0");
    expect(text).toContain("out 2.0 kB");
    expect(text).toContain("err 0 B");
  });

  it("non-zero exit → AMBER (warn), NOT failure — a non-zero exit is a branchable outcome, not a node failure", () => {
    const digest = pipelineDigest("agent.run_command", rowWith({ exitCode: 1, status: "Failed", stdoutBytes: 100, stderrBytes: 40 }));
    expect(digest?.tone).toBe("warn");
    expect(labelText(digest)).toContain("exit 1");
  });

  it("TimedOut → a clock glyph + 逾時 (warn)", () => {
    const digest = pipelineDigest("agent.run_command", rowWith({ exitCode: -1, status: "TimedOut", stdoutBytes: 0, stderrBytes: 0 }));
    expect(digest?.tone).toBe("warn");
    expect(labelText(digest)).toContain("逾時");
  });

  it("shows a 📎 when the full output was preserved as an artifact", () => {
    const digest = pipelineDigest("agent.run_command", rowWith({ exitCode: 0, status: "Success", stdoutBytes: 999999, stderrBytes: 0, stdoutArtifactId: "11111111-1111-1111-1111-111111111111" }));
    expect(labelText(digest)).toContain("📎");
  });

  it("returns null when neither an exit code nor a timeout landed", () => {
    expect(pipelineDigest("agent.run_command", rowWith({ stdoutBytes: 10 }))).toBeNull();
  });
});

describe("pipelineDigest — degrade", () => {
  it("returns null for an unknown type", () => {
    expect(pipelineDigest("git.something_new", rowWith({ status: "Clean" }))).toBeNull();
  });

  it("returns null when the row carries no keyed outputs", () => {
    expect(pipelineDigest("git.integrate", rowWith(null))).toBeNull();
    expect(pipelineDigest("agent.run_command", rowWith("not-an-object"))).toBeNull();
    expect(pipelineDigest("git.integrate", [])).toBeNull();
  });
});

/** A minimal run-live store exposing one node's signals — exercises the REAL useNodeLiveContext path via context. */
function fakeLiveStore(nodeId: string, signals: NodeLiveSignals): RunLiveStore {
  const state: RunLiveState = { byNode: new Map([[nodeId, signals]]), lastSeq: 0, terminal: false };
  return { getState: () => state, subscribe: () => () => {} };
}

/** A minimal node data blob — the footer reads only nodeId / typeKey / displayName / label / config. */
function nodeData(overrides: Partial<WorkflowNodeData>): WorkflowNodeData {
  return { nodeId: "n1", typeKey: "git.integrate", displayName: "Integrate", iconKey: null, kind: "Regular", category: "Git", label: null, ...overrides };
}

function renderFooter(status: NodeStatus, data: WorkflowNodeData, rows: WorkflowRunNodeSummary[], store?: RunLiveStore) {
  const footer = <PipelineFooter data={data} status={status} rows={rows} title={data.displayName} />;
  return render(store ? <RunLiveContext.Provider value={store}>{footer}</RunLiveContext.Provider> : footer);
}

describe("PipelineFooter — git.integrate", () => {
  it("Running renders a STATIC 5-stage rail + one indeterminate shimmer (no per-stage lighting) + elapsed", () => {
    const startedAt = new Date(FIXED_NOW - 65_000).toISOString();   // 65s elapsed → "1:05"
    const { container } = renderFooter("Running", nodeData({}), rowWith({}, startedAt));

    const stages = container.querySelectorAll(".wf-pf-stage");
    expect(stages).toHaveLength(5);
    // No stage carries a lit / active / done marker — progress isn't observable, so nothing is faked.
    stages.forEach((s) => {
      expect(s.getAttribute("data-state")).toBeNull();
      expect(s.getAttribute("data-active")).toBeNull();
    });
    expect(container.querySelector(".wf-pf-sweep")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-dur")?.textContent).toBe("1:05");
  });

  it("terminal Conflicted → amber outcome + reassurance line + the first conflict (the node still SUCCEEDS)", () => {
    const rows = rowWith({
      status: "Conflicted",
      integratedBranch: null,
      appliedCount: 0,
      conflicts: [{ label: "fix-auth", disposition: "Conflicted", reason: "base SHA mismatch" }],
    });
    // git.integrate SUCCEEDS on a Conflicted result — the node status is Success, the OUTCOME is the branchable part.
    const { container } = renderFooter("Success", nodeData({}), rows);

    expect(container.querySelector(".wf-pf-int")?.getAttribute("data-outcome")).toBe("conflicted");
    expect(container.querySelector(".wf-rf-digest")?.getAttribute("data-tone")).toBe("warn");
    expect(container.querySelector(".wf-pf-reassure")?.textContent).toContain("什麼都沒推");
    expect(container.textContent).toContain("fix-auth");
    expect(container.textContent).toContain("base SHA mismatch");
  });

  it("terminal Clean → green outcome + branch chip", () => {
    const rows = rowWith({ status: "Clean", integratedBranch: "codespace/integration/run-9", appliedCount: 2, conflicts: [] });
    const { container } = renderFooter("Success", nodeData({}), rows);

    expect(container.querySelector(".wf-pf-int")?.getAttribute("data-outcome")).toBe("clean");
    expect(container.querySelector(".wf-pf-branch")?.textContent).toContain("codespace/integration/run-9");
    expect(container.textContent).toContain("2/2 applied");
  });

  it("null-store Running renders the rail without throwing (no live provider)", () => {
    const { container } = renderFooter("Running", nodeData({}), rowWith({}));
    expect(container.querySelectorAll(".wf-pf-stage")).toHaveLength(5);
  });
});

describe("PipelineFooter — agent.run_command", () => {
  const cmdData = (overrides: Partial<WorkflowNodeData> = {}) => nodeData({ typeKey: "agent.run_command", displayName: "Run command", category: "Agent", ...overrides });

  it("Running renders the blinking terminal cursor + the timeout ring when config carries a timeout", () => {
    // 15s elapsed of a 30s timeout → 50% ring, off the live command span's start.
    const store = fakeLiveStore("n1", { call: { target: "agent.run_command:ephemeral", method: "run_command", startedAtMs: FIXED_NOW - 15_000 }, lastEventSeq: 1 });
    const data = cmdData({ config: { timeoutSeconds: 30 } } as Partial<WorkflowNodeData>);
    const { container } = renderFooter("Running", data, [], store);

    expect(container.querySelector(".wf-pf-cursor")).not.toBeNull();
    const ring = container.querySelector(".wf-ring") as HTMLElement | null;
    expect(ring).not.toBeNull();
    expect(ring?.style.getPropertyValue("--wf-ring-p")).toBe("50%");
  });

  it("Running without a config timeout shows the cursor but NO ring (degrades gracefully)", () => {
    const store = fakeLiveStore("n1", { call: { target: "agent.run_command:ephemeral", method: "run_command", startedAtMs: FIXED_NOW - 5_000 }, lastEventSeq: 1 });
    const { container } = renderFooter("Running", cmdData(), [], store);

    expect(container.querySelector(".wf-pf-cursor")).not.toBeNull();
    expect(container.querySelector(".wf-ring")).toBeNull();
    expect(container.querySelector(".wf-rf-result-dur")?.textContent).toBe("0:05");
  });

  it("terminal exit 1 → an AMBER exit stamp (the node SUCCEEDS; a non-zero exit is branchable, not red)", () => {
    const rows = rowWith({ exitCode: 1, status: "Failed", stdoutBytes: 320, stderrBytes: 12 });
    const { container } = renderFooter("Success", cmdData(), rows);

    const digest = container.querySelector(".wf-rf-digest");
    expect(digest?.getAttribute("data-tone")).toBe("warn");
    expect(digest?.textContent).toContain("exit 1");
  });

  it("null-store Running renders the cursor without throwing (no live provider)", () => {
    const { container } = renderFooter("Running", cmdData(), rowWith({}, new Date(FIXED_NOW - 5_000).toISOString()));
    expect(container.querySelector(".wf-pf-cursor")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-dur")?.textContent).toBe("0:05");
  });
});
