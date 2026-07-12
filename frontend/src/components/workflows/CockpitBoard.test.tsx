import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PendingDecision, RunPhase, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { CockpitBoard, type RunAttentionView, type RunHistoryView } from "./CockpitBoard";

// DecisionCard children answer through this hook — stub it so the board renders without a QueryClient.
vi.mock("@/hooks/use-workflows", () => ({ useAnswerDecision: () => ({ mutate: vi.fn(), isPending: false }) }));

const NOW = new Date(2026, 5, 22, 15, 0, 0).getTime();

function run(id: string, status: WorkflowRunStatus, o: Partial<WorkflowRunSummary> = {}): WorkflowRunSummary {
  const r = { id, runNumber: 1, workflowId: "w", workflowVersion: 1, workflowName: null, sessionTitle: null, sourceType: "manual", status, error: null, startedAt: new Date(NOW - 18 * 60_000).toISOString(), completedAt: null, createdDate: new Date(NOW).toISOString(), rootRunId: id, attemptCount: 1, hasSession: true, ...o };
  return { ...r, rootSourceType: o.rootSourceType ?? r.sourceType };   // a non-rerun run's root source == its own
}

function decision(o: Partial<PendingDecision>): PendingDecision {
  return { id: "d", grain: "tool_ledger", rootTraceId: "r", decisionType: "confirm", question: "?", options: [], riskLevel: "low", policy: "human_required", createdAt: new Date(NOW).toISOString(), ...o };
}

function phasesFor(runId: string): RunPhasesResponse {
  const phase: RunPhase = {
    id: "impl", label: "Implement", kind: "node", status: "Active", order: 0,
    agents: [{ agentRunId: "a1", status: "Running" }, { agentRunId: "a2", status: "Running" }, { agentRunId: "a3", status: "Succeeded" }],
    metrics: { agentCount: 3, succeededCount: 1, failedCount: 0 }, sourceKey: "s",
  };
  return { runId, runStatus: "Running", phases: [phase] };
}

// History reads its OWN paginated view; the Needs-attention zone reads its OWN fetched set (suspended-needing-review)
// with the true total. Tests pass `hist([...])` / `attn([...])` for the zone they exercise; everything else is empty.
const HISTORY_EMPTY: RunHistoryView = { items: [], total: 0, page: 1, pageSize: 20, isLoading: false, onPage: () => {} };
const hist = (items: WorkflowRunSummary[], extra: Partial<RunHistoryView> = {}): RunHistoryView => ({ ...HISTORY_EMPTY, items, total: items.length, ...extra });
const ATTENTION_EMPTY: RunAttentionView = { runs: [], total: 0 };
const attn = (items: WorkflowRunSummary[], total?: number): RunAttentionView => ({ runs: items, total: total ?? items.length });

const board = (o: Partial<Parameters<typeof CockpitBoard>[0]>) =>
  render(<CockpitBoard runs={[]} decisions={[]} live={[]} attention={ATTENTION_EMPTY} phasesByRun={new Map()} filter={null} history={HISTORY_EMPTY} nowMs={NOW} onOpen={() => {}} onFilter={() => {}} {...o} />);

describe("CockpitBoard", () => {
  it("shows the three zones, the answerable decision, and a live state sentence", () => {
    const { container } = board({
      live: [run("live1", "Running")],
      decisions: [decision({ id: "dec1", question: "Choose merge strategy" })],
      phasesByRun: new Map([["live1", phasesFor("live1")]]),
      history: hist([run("done1", "Success")]),
    });

    expect(screen.getByText("Needs attention")).toBeTruthy();
    expect(screen.getByText("Choose merge strategy")).toBeTruthy();   // decision card
    expect(screen.getByText("Live")).toBeTruthy();
    expect(screen.getByText(/Implement · 2 of 3 agents active/)).toBeTruthy();   // live sentence from phases
    expect(screen.getByText("History")).toBeTruthy();
    expect(container.querySelectorAll(".run-row2").length).toBe(1);    // the one Success run, from the History page
    const recent = container.querySelector(".run-row2")!;
    expect(recent.querySelector(".run-row2-type")?.textContent).toBe("Workflow");   // type label beside the name
    expect(recent.querySelector(".run-row2-ver")?.textContent).toBe("v1");          // version label
    expect(recent.querySelector(".run-row2-sw")?.textContent).toBe("Success");      // status word in its tone
  });

  it("titles a row with the workflow name, then the session title, then a neutral fallback — never the source token", () => {
    const { container } = board({ history: hist([
      run("named", "Success", { workflowName: "Deploy Pipeline" }),
      run("task", "Success", { workflowName: null, sessionTitle: "Remove unused usings" }),
      run("anon", "Success", { workflowName: null, sessionTitle: null, sourceType: "snapshot" }),
    ]) });

    const titles = [...container.querySelectorAll(".run-row2-title")].map((n) => n.textContent);
    expect(titles).toContain("Deploy Pipeline");        // authored run → its workflow name
    expect(titles).toContain("Remove unused usings");   // task run → its session title, never the "snapshot" token
    expect(titles).toContain("Untitled task");          // no name + no session → neutral fallback, never a source label
  });

  it("renders the Needs-attention zone from its own fetched suspended set (decisions + suspended rows)", () => {
    const { container } = board({
      decisions: [decision({ id: "dx" })],
      attention: attn([run("s1", "Suspended"), run("s2", "Suspended")]),
    });
    expect(container.querySelectorAll(".cockpit-attn-row").length).toBe(2);   // both suspended runs the zone was given
  });

  it("previews the first few suspended runs and offers 'View all N' (the true total) when there are more", () => {
    const onFilter = vi.fn();
    const many = Array.from({ length: 5 }, (_, i) => run(`s${i}`, "Suspended"));
    const { container } = board({ attention: attn(many, 12), decisions: [decision({ id: "d1" }), decision({ id: "d2" })], onFilter });

    expect(container.querySelectorAll(".cockpit-attn-row").length).toBe(5);   // preview cap
    const viewAll = screen.getByText(/View all 14/);   // 12 suspended + 2 decisions = the card's true total
    fireEvent.click(viewAll);
    expect(onFilter).toHaveBeenCalledWith("attention");
  });

  it("notes the cap in the armed attention view when more suspended runs exist than were fetched (never over-promises)", () => {
    const fetched = Array.from({ length: 50 }, (_, i) => run(`s${i}`, "Suspended"));
    const { container } = board({ filter: "attention", attention: attn(fetched, 60) });   // 60 true, only 50 fetched

    expect(container.querySelectorAll(".cockpit-attn-row").length).toBe(50);   // the fetched set
    expect(screen.getByText(/Showing the first 50 of 60/)).toBeInTheDocument();   // honest about the rest
  });

  it("opens a run from a Review action and from a recent row", () => {
    const onOpen = vi.fn();
    const { container } = board({ attention: attn([run("s1", "Suspended")]), history: hist([run("r1", "Success")]), onOpen });

    fireEvent.click(screen.getByText("Review →"));
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ rootRunId: "s1" }));

    fireEvent.click(container.querySelector(".run-row2")!);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ rootRunId: "r1" }));
  });

  it("a reran row shows the ORIGINAL run's id + title and opens the original, not the fork", () => {
    const onOpen = vi.fn();
    // The representative is the latest fork (own id forkrun9, sourceType replay) but the lineage root is origrun1.
    const { container } = board({ history: hist([run("forkrun9", "Success", { rootRunId: "origrun1", attemptCount: 3, workflowName: null, sessionTitle: "Remove unused usings", sourceType: "replay", rootSourceType: "snapshot" })]), onOpen });

    expect(container.querySelector(".run-row2-id")?.textContent).toBe("origrun1");   // the original's id, not the fork's
    expect(container.querySelector(".run-row2-title")?.textContent).toBe("Remove unused usings");  // titles as the task's own goal, never "Replay" or the "snapshot" token
    expect(container.querySelector(".run-row2-attempts")?.textContent).toContain("3 attempts");

    fireEvent.click(container.querySelector(".run-row2")!);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ rootRunId: "origrun1" }));   // opens the original — no "Replay of …"
  });

  it("a live rerun shows a 'rerunning · attempt N' brief and opens the original", () => {
    const onOpen = vi.fn();
    const { container } = board({ live: [run("livefork", "Running", { rootRunId: "origin01", attemptCount: 2 })], onOpen });

    const brief = container.querySelector(".cockpit-live-rerun")?.textContent ?? "";
    expect(brief).toContain("rerunning");
    expect(brief).toContain("attempt 2");

    fireEvent.click(container.querySelector(".cockpit-live-row")!);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ rootRunId: "origin01" }));
  });

  it("a non-rerun live run shows no rerunning brief", () => {
    const { container } = board({ live: [run("solo", "Running")] });   // rootRunId === id → not a rerun
    expect(container.querySelector(".cockpit-live-rerun")).toBeNull();
  });

  it("filter='failed' shows only failed runs (suspended lives in Needs attention)", () => {
    const { container } = board({ filter: "failed", runs: [run("f", "Failure"), run("s", "Suspended"), run("ok", "Success"), run("live", "Running")] });
    expect(container.querySelector(".cockpit-zone-label")?.textContent).toBe("Failed");   // zone label (distinct from the run's "Failed" status word)
    expect(container.querySelectorAll(".run-row2").length).toBe(1);   // Failure only
  });

  it("falls back to the run status in the Live row before its phases + start load", () => {
    const { container } = board({ live: [run("live1", "Running", { startedAt: null })], phasesByRun: new Map() });   // no phases, no start
    const meta = container.querySelector(".cockpit-live-meta");
    expect(meta?.textContent).toBe("Running");   // degrades to status, no crash
  });

  it("renders the Live zone from its own `live` set (including auto-resuming suspends), not from `runs`", () => {
    const { container } = board({
      runs: [run("ignored", "Failure", { workflowName: "Not in live" })],   // the `runs` prop no longer feeds the default board's Live zone
      live: [run("working", "Running", { workflowName: "Fan out" }), run("parked", "Suspended", { workflowName: "Snapshot" })],
    });
    const titles = [...container.querySelectorAll(".cockpit-live-title")].map((n) => n.textContent);
    expect(titles).toEqual(["Fan out", "Snapshot"]);   // both from `live`; a Suspended run here is auto-resuming → still Live, not attention
  });

  it("filter='today' shows only runs created today", () => {
    const yesterday = new Date(NOW - 36 * 3_600_000).toISOString();
    const { container } = board({ filter: "today", runs: [
      run("t1", "Success", { createdDate: new Date(NOW).toISOString() }),
      run("t2", "Success", { createdDate: yesterday }),
    ] });
    expect(screen.getByText("Today")).toBeTruthy();
    expect(container.querySelectorAll(".run-row2").length).toBe(1);   // only t1 (today)
  });

  it("shows the calm empty state when nothing needs attention", () => {
    board({ runs: [run("ok", "Success")], decisions: [] });
    expect(screen.getByText(/Nothing needs you/)).toBeTruthy();
  });

  it("labels the Workflow/Task type + version beside the name, and shows the status word + run duration", () => {
    const { container } = board({ history: hist([
      run("wf", "Success", { workflowVersion: 3, createdDate: new Date(NOW - 7 * 60_000 - 59_000).toISOString(), completedAt: new Date(NOW).toISOString() }),
      run("task", "Success", { workflowId: null, workflowVersion: null }),
    ]) });

    const rows = [...container.querySelectorAll(".run-row2")];
    const wf = rows.find((r) => r.querySelector(".run-row2-type")?.textContent === "Workflow")!;
    expect(wf.querySelector(".run-row2-type")?.getAttribute("data-type")).toBe("workflow");   // coral type chip
    expect(wf.querySelector(".run-row2-ver")?.textContent).toBe("v3");                         // version is its own label
    expect(wf.querySelector(".run-row2-dur")?.textContent).toContain("7m 59s");                // duration shown on success

    const task = rows.find((r) => r.querySelector(".run-row2-type")?.textContent === "Task")!;
    expect(task.querySelector(".run-row2-type")?.getAttribute("data-type")).toBe("task");
    expect(task.querySelector(".run-row2-ver")).toBeNull();                                     // a task run has no version label
  });

  it("shows an 'N attempts' chip only on a collapsed lineage (attemptCount > 1)", () => {
    const { container } = board({ history: hist([
      run("rerun", "Success", { attemptCount: 3 }),   // a run + 2 reruns, collapsed to this latest attempt
      run("once", "Success", { attemptCount: 1 }),    // a never-rerun run
    ]) });

    const rows = [...container.querySelectorAll(".run-row2")];
    const collapsed = rows.find((r) => r.querySelector(".run-row2-id")?.textContent === "rerun")!;
    const single = rows.find((r) => r.querySelector(".run-row2-id")?.textContent === "once")!;

    expect(collapsed.querySelector(".run-row2-attempts")?.textContent).toContain("3 attempts");
    expect(single.querySelector(".run-row2-attempts")).toBeNull();   // no chip when there's only one attempt
  });

  it("boxes a failed run's error on a third line, with the duration still shown", () => {
    const { container } = board({ history: hist([
      run("f", "Failure", { error: "check.sh exit 1 — 2 tests failing", createdDate: new Date(NOW - 4 * 60_000 - 11_000).toISOString(), completedAt: new Date(NOW).toISOString() }),
    ]) });

    const row = container.querySelector(".run-row2")!;
    expect(row.querySelector(".run-row2-sw")?.textContent).toBe("Failed");
    expect(row.querySelector(".run-row2-dur")?.textContent).toContain("4m 11s");           // failure shows its run time too
    expect(row.querySelector(".run-row2-err")?.textContent).toContain("check.sh exit 1 — 2 tests failing");
  });

  it("shows no error line for a failed run without a message, nor for a success", () => {
    const { container } = board({ history: hist([run("f", "Failure", { error: null }), run("ok", "Success")]) });
    expect(container.querySelectorAll(".run-row2-err").length).toBe(0);   // no error string → no third line
  });

  it("shows the History pager across multiple pages and reports the clicked page", () => {
    const onPage = vi.fn();
    board({ history: hist([run("h1", "Success"), run("h2", "Success")], { total: 45, page: 1, pageSize: 20, onPage }) });

    fireEvent.click(screen.getByRole("button", { name: "2" }));   // 45 rows / 20 per page = 3 pages → numbered buttons
    expect(onPage).toHaveBeenCalledWith(2);
  });

  it("hides the History pager when everything fits on one page", () => {
    const { container } = board({ history: hist([run("h1", "Success")], { total: 1 }) });
    expect(container.querySelector(".runs-pager")).toBeNull();
  });

  it("shows a loading state for the History zone while its first page loads", () => {
    board({ history: hist([], { isLoading: true }) });
    expect(screen.getByText("Loading…")).toBeTruthy();   // not "No past runs yet." — we don't yet know it's empty
  });
});
