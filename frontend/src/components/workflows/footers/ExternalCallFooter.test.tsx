import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { RunLiveContext, type RunLiveStore } from "@/hooks/use-run-live";
import type { NodeLiveSignals, RunLiveState } from "@/lib/runLiveFold";

import type { WorkflowNodeData } from "../WorkflowNode";
import { digestExternalCall, ExternalCallFooter, type ExternalCallDigest } from "./ExternalCallFooter";

// Freeze the shared clock so the live elapsed is deterministic; keep the real formatElapsed.
const FIXED_NOW = 1_000_000;
vi.mock("@/hooks/use-now-tick", async (importActual) => {
  const actual = await importActual<typeof import("@/hooks/use-now-tick")>();
  return { ...actual, useNowTick: () => FIXED_NOW };
});

/** Build a settled run row carrying the given outputs — the only field the digest reads. */
function rowWith(outputs: unknown): WorkflowRunNodeSummary[] {
  return [{ nodeId: "n", iterationKey: "", status: "Success", inputs: {}, outputs, error: null, startedAt: null, completedAt: null }];
}

/** Render a digest label to plain text so the per-type formatter can be asserted without DOM plumbing. */
function labelText(digest: ExternalCallDigest | null): string {
  if (!digest) return "";
  return render(<>{digest.label}</>).container.textContent ?? "";
}

describe("digestExternalCall — per-type receipt formatters", () => {
  it("git.open_pr → #number opened with a link to the created url (success)", () => {
    const digest = digestExternalCall("git.open_pr", rowWith({ number: 42, url: "https://github.com/x/y/pull/42", state: "Open" }));
    expect(digest?.tone).toBe("success");

    const { container } = render(<>{digest?.label}</>);
    expect(container.textContent).toContain("#42");
    expect(container.textContent).toContain("opened");
    expect(container.querySelector("a")?.getAttribute("href")).toBe("https://github.com/x/y/pull/42");
  });

  it("git.create_issue → #number opened (shares the opened formatter)", () => {
    const digest = digestExternalCall("git.create_issue", rowWith({ number: 7, url: "https://gl/x/-/issues/7" }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toContain("#7");
  });

  it("git.merge_pr merged → merged · sha7 (success)", () => {
    const digest = digestExternalCall("git.merge_pr", rowWith({ merged: true, sha: "a1b2c3d4e5f6", message: "Merged" }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("merged · a1b2c3d");
  });

  it("git.merge_pr not merged → provider reason as warn, not failure", () => {
    const digest = digestExternalCall("git.merge_pr", rowWith({ merged: false, message: "Merge conflict" }));
    expect(digest?.tone).toBe("warn");
    expect(labelText(digest)).toBe("Merge conflict");
  });

  it("git.fetch_pr_checks allPassed → passing/total · state (success)", () => {
    const digest = digestExternalCall("git.fetch_pr_checks", rowWith({ passing: 5, total: 5, state: "success", allPassed: true, failing: 0, pending: 0 }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("5/5 · success");
  });

  it("git.fetch_pr_checks with a failing check → failure", () => {
    const digest = digestExternalCall("git.fetch_pr_checks", rowWith({ passing: 3, total: 5, state: "failure", allPassed: false, failing: 2, pending: 0 }));
    expect(digest?.tone).toBe("failure");
  });

  it("git.fetch_pr_checks pending (none failing) → warn", () => {
    const digest = digestExternalCall("git.fetch_pr_checks", rowWith({ passing: 3, total: 5, state: "pending", allPassed: false, failing: 0, pending: 2 }));
    expect(digest?.tone).toBe("warn");
  });

  it("git.fetch_pr_diff → files files · +add −del", () => {
    const digest = digestExternalCall("git.fetch_pr_diff", rowWith({ files: [{}, {}, {}], additions: 10, deletions: 4 }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("3 files · +10 −4");
  });

  it("git.list_prs → count PRs", () => {
    expect(labelText(digestExternalCall("git.list_prs", rowWith({ count: 7 })))).toBe("7 PRs");
  });

  it("git.pr_review → the verdict word; request_changes reads as warn", () => {
    expect(labelText(digestExternalCall("git.pr_review", rowWith({ verdict: "approve" })))).toBe("approve");
    expect(digestExternalCall("git.pr_review", rowWith({ verdict: "approve" }))?.tone).toBe("success");
    expect(digestExternalCall("git.pr_review", rowWith({ verdict: "request_changes" }))?.tone).toBe("warn");
  });

  it("git.post_pr_comment with webUrl → Published + link", () => {
    const digest = digestExternalCall("git.post_pr_comment", rowWith({ commentId: "c1", webUrl: "https://github.com/x/y/pull/1#note-1" }));
    const { container } = render(<>{digest?.label}</>);
    expect(container.textContent).toContain("Published");
    expect(container.querySelector("a")?.getAttribute("href")).toBe("https://github.com/x/y/pull/1#note-1");
  });

  it("git.comment_issue on GitLab (no webUrl) → Published with NO link", () => {
    const digest = digestExternalCall("git.comment_issue", rowWith({ commentId: "c2", webUrl: null }));
    const { container } = render(<>{digest?.label}</>);
    expect(container.textContent).toContain("Published");
    expect(container.querySelector("a")).toBeNull();
  });

  it("git.close_issue → the resulting state", () => {
    expect(labelText(digestExternalCall("git.close_issue", rowWith({ number: 9, state: "closed", url: null })))).toBe("closed");
  });

  it("http.request 2xx → status + ok (success)", () => {
    const digest = digestExternalCall("http.request", rowWith({ status: 200, ok: true }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("200 ok");
  });

  it("http.request non-2xx → warn, NOT failure (ok:false is branchable, not an error)", () => {
    const digest = digestExternalCall("http.request", rowWith({ status: 404, ok: false }));
    expect(digest?.tone).toBe("warn");
    expect(labelText(digest)).toBe("404 not ok");
  });

  it("returns null for an unknown type", () => {
    expect(digestExternalCall("git.something_new", rowWith({ number: 1 }))).toBeNull();
  });

  it("returns null when the row carries no keyed outputs", () => {
    expect(digestExternalCall("git.open_pr", rowWith(null))).toBeNull();
    expect(digestExternalCall("git.open_pr", rowWith("not-an-object"))).toBeNull();
    expect(digestExternalCall("http.request", [])).toBeNull();
  });
});

/** A minimal run-live store exposing one node's signals — exercises the REAL useNodeLiveContext path via context. */
function fakeLiveStore(nodeId: string, signals: NodeLiveSignals): RunLiveStore {
  const state: RunLiveState = { byNode: new Map([[nodeId, signals]]), lastSeq: 0, terminal: false };
  return { getState: () => state, subscribe: () => () => {} };
}

/** A minimal node data blob — the footer reads only nodeId / typeKey / displayName / label. */
function nodeData(overrides: Partial<WorkflowNodeData>): WorkflowNodeData {
  return { nodeId: "n1", typeKey: "http.request", displayName: "HTTP Request", iconKey: null, kind: "Regular", category: "Tools", label: null, ...overrides };
}

function renderFooter(status: NodeStatus, data: WorkflowNodeData, rows: WorkflowRunNodeSummary[], store?: RunLiveStore) {
  const footer = <ExternalCallFooter data={data} status={status} rows={rows} title={data.displayName} />;
  return render(store ? <RunLiveContext.Provider value={store}>{footer}</RunLiveContext.Provider> : footer);
}

describe("ExternalCallFooter — component", () => {
  it("Running with a live call renders the spinner, verb/target, and ticking elapsed", () => {
    const store = fakeLiveStore("n1", { call: { target: "https://api.github.com/repos", method: "POST", startedAtMs: FIXED_NOW - 65_000 }, lastEventSeq: 1 });
    const { container } = renderFooter("Running", nodeData({ nodeId: "n1", typeKey: "git.open_pr" }), [], store);

    expect(container.querySelector(".wf-rf-status-spin")).not.toBeNull();
    const label = container.querySelector(".wf-rf-result-label")?.textContent ?? "";
    expect(label).toContain("POST");
    expect(label).toContain("api.github.com");
    expect(container.querySelector(".wf-rf-result-dur")?.textContent).toBe("1:05");
  });

  it("Running http.request shows the timeout ring when config carries a timeout", () => {
    const store = fakeLiveStore("n1", { call: { target: "https://x/y", method: "GET", startedAtMs: FIXED_NOW - 15_000 }, lastEventSeq: 1 });
    // 15s elapsed of a 30s timeout → 50% ring.
    const data = nodeData({ nodeId: "n1", typeKey: "http.request", config: { timeoutSeconds: 30 } } as Partial<WorkflowNodeData>);
    const { container } = renderFooter("Running", data, [], store);

    const ring = container.querySelector(".wf-ring") as HTMLElement | null;
    expect(ring).not.toBeNull();
    expect(ring?.style.getPropertyValue("--wf-ring-p")).toBe("50%");
  });

  it("terminal Success renders the per-type digest label in the reused receipt bar", () => {
    const rows = rowWith({ number: 42, url: "https://github.com/x/y/pull/42" });
    rows[0].status = "Success";
    const { container } = renderFooter("Success", nodeData({ nodeId: "n1", typeKey: "git.open_pr" }), rows);

    expect(container.querySelector(".wf-rf-result")?.getAttribute("data-status")).toBe("success");
    expect(container.querySelector(".wf-rf-digest")?.textContent).toContain("#42");
    expect(container.textContent).toContain("opened");
  });

  it("a live-less Running (no store / no provider) renders degraded without throwing", () => {
    const { container } = renderFooter("Running", nodeData({ nodeId: "n1", typeKey: "http.request" }), []);

    expect(container.querySelector(".wf-rf-status-spin")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("HTTP Request");
    // No live call → no elapsed, no ring.
    expect(container.querySelector(".wf-rf-result-dur")).toBeNull();
    expect(container.querySelector(".wf-ring")).toBeNull();
  });
});
