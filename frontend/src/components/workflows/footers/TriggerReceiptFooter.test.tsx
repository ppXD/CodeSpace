import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { WorkflowRunNodeSummary } from "@/api/workflows";

import type { WorkflowNodeData } from "../WorkflowNode";
import { TriggerReceiptFooter, triggerDigest, type TriggerDigest } from "./TriggerReceiptFooter";

/** Build a settled trigger row carrying the given outputs — the only field the digest reads. */
function rowWith(outputs: unknown): WorkflowRunNodeSummary[] {
  return [{ nodeId: "t", iterationKey: "", status: "Success", inputs: {}, outputs, error: null, startedAt: null, completedAt: null }];
}

/** Render a digest label to plain text so the per-type formatter can be asserted without DOM plumbing. */
function labelText(digest: TriggerDigest | null): string {
  if (!digest) return "";
  return render(<>{digest.label}</>).container.textContent ?? "";
}

describe("triggerDigest — per-trigger receipt formatters", () => {
  it("trigger.pr.opened → #number + title, author appended when present", () => {
    const digest = triggerDigest("trigger.pr.opened", rowWith({ number: 42, title: "Fix the flaky login test", author: "alice" }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("#42Fix the flaky login test · alice");
  });

  it("trigger.pr.updated → shares the pr formatter and truncates a long title", () => {
    const digest = triggerDigest("trigger.pr.updated", rowWith({ number: 7, title: "A really quite excessively long pull request title that keeps going" }));
    expect(labelText(digest)).toBe("#7A really quite excessively long…");   // 32-char cap + ellipsis
  });

  it("trigger.pr.merged → number alone when no title landed", () => {
    const digest = triggerDigest("trigger.pr.merged", rowWith({ number: 9 }));
    expect(labelText(digest)).toBe("#9");
  });

  it("trigger.pr.* → null when neither a number nor a title is present", () => {
    expect(triggerDigest("trigger.pr.opened", rowWith({ author: "bob" }))).toBeNull();
  });

  it("trigger.push → branch · commitCount commits · afterSha7", () => {
    const digest = triggerDigest("trigger.push", rowWith({ branch: "main", commitCount: 3, after: "a1b2c3d4e5f6" }));
    expect(digest?.tone).toBe("success");
    expect(labelText(digest)).toBe("main · 3 commits · a1b2c3d");
  });

  it("trigger.push → derives the commit count from a commits array and drops absent parts", () => {
    const digest = triggerDigest("trigger.push", rowWith({ branch: "dev", commits: [{}, {}] }));
    expect(labelText(digest)).toBe("dev · 2 commits");
  });

  it("trigger.push → null when the whole push output is empty", () => {
    expect(triggerDigest("trigger.push", rowWith({}))).toBeNull();
  });

  it("trigger.schedule → the scheduledFor timestamp verbatim", () => {
    const digest = triggerDigest("trigger.schedule", rowWith({ scheduledFor: "2026-07-13T09:00:00Z" }));
    expect(labelText(digest)).toBe("2026-07-13T09:00:00Z");
  });

  it("trigger.schedule → null when scheduledFor is absent", () => {
    expect(triggerDigest("trigger.schedule", rowWith({}))).toBeNull();
  });

  it("trigger.manual → 由 {actor} when the actor is known", () => {
    const digest = triggerDigest("trigger.manual", rowWith({ actor: "carol" }));
    expect(labelText(digest)).toBe("由 carol");
  });

  it("trigger.manual → 手動 when no actor but an outputs object is present", () => {
    expect(labelText(triggerDigest("trigger.manual", rowWith({})))).toBe("手動");
  });

  it("returns null for a non-trigger typeKey", () => {
    expect(triggerDigest("llm.complete", rowWith({ number: 1 }))).toBeNull();
  });

  it("returns null when outputs are missing entirely (null / non-object)", () => {
    expect(triggerDigest("trigger.manual", rowWith(null))).toBeNull();
    expect(triggerDigest("trigger.push", rowWith(["not", "an", "object"]))).toBeNull();
  });
});

/** Minimal node data for the footer wiring test. */
function triggerData(typeKey: string): WorkflowNodeData {
  return { nodeId: "t", typeKey, displayName: "Trigger", iconKey: null, kind: "Trigger", category: "Triggers", label: null };
}

describe("TriggerReceiptFooter wiring", () => {
  it("injects the digest as the receipt label when the trigger has SUCCEEDED", () => {
    const rows = rowWith({ number: 42, title: "Fix bug", author: "alice" });
    const { container } = render(<TriggerReceiptFooter data={triggerData("trigger.pr.opened")} status="Success" rows={rows} />);

    const label = container.querySelector(".wf-rf-result-label");
    expect(label?.querySelector(".wf-rf-digest")).not.toBeNull();
    expect(label?.textContent).toBe("#42Fix bug · alice");
  });

  it("falls back to the plain status label for a non-Success trigger (no digest while running)", () => {
    const rows: WorkflowRunNodeSummary[] = [{ nodeId: "t", iterationKey: "", status: "Running", inputs: {}, outputs: { number: 42 }, error: null, startedAt: null, completedAt: null }];
    const { container } = render(<TriggerReceiptFooter data={triggerData("trigger.pr.opened")} status="Running" rows={rows} />);

    expect(container.querySelector(".wf-rf-digest")).toBeNull();
    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("Running");
  });

  it("passes a non-trigger receipt-kind node straight through to the plain bar", () => {
    const rows = rowWith({ anything: true });
    const data: WorkflowNodeData = { nodeId: "l", typeKey: "flow.loop", displayName: "Loop", iconKey: null, kind: "Loop", category: "Logic", label: null };
    const { container } = render(<TriggerReceiptFooter data={data} status="Success" rows={rows} />);

    expect(container.querySelector(".wf-rf-digest")).toBeNull();
    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("Success");
  });
});
