import { describe, expect, it } from "vitest";
import type { JournalStep, JournalTurn, JournalView } from "@/api/sessions";
import { mergeJournalDelta } from "@/lib/journalDelta";

/// Unit: the `?since=` journal delta consumer. Pins that new steps land in order, that steps the server trimmed as
/// already-held survive the merge, and — the property the whole trim rests on — that ANY divergence between what the
/// client accumulated and the server's `stepCount` returns null so the caller re-fetches in full instead of rendering a
/// journal with a hole in it.
describe("mergeJournalDelta", () => {
  const step = (id: string): JournalStep =>
    ({ id, at: "2026-08-19T12:00:00Z", kind: "lifecycle", title: id, cursor: id, tone: "neutral", milestone: false, agents: [], deferred: [], plan: [] }) as unknown as JournalStep;

  const turn = (over: Partial<JournalTurn>): JournalTurn =>
    ({ turnIndex: 1, turnRunId: "t1", runId: "r1", status: "Running", focused: true, steps: [], stepCount: 0, attempts: [], ...over }) as unknown as JournalTurn;

  const view = (turns: JournalTurn[], cursor = "c"): JournalView =>
    ({ sessionId: "s1", title: "t", kind: "Task", status: "Open", cursor, turns }) as unknown as JournalView;

  it("appends the focused turn's new steps after the ones already held", () => {
    const prior = view([turn({ steps: [step("s1"), step("s2")], stepCount: 2 })]);
    const delta = view([turn({ steps: [step("s3")], stepCount: 3 })], "c2");

    const merged = mergeJournalDelta(prior, delta);

    expect(merged?.turns[0].steps.map((s) => s.id)).toEqual(["s1", "s2", "s3"]);
    expect(merged?.cursor).toBe("c2");
  });

  it("keeps the steps a finished turn's trim omitted", () => {
    // The server drops a non-focused TERMINAL turn's steps because its walk can never change. The client's held copy is
    // therefore still correct, and stepCount is how it confirms that rather than assuming it.
    const held = [step("c1"), step("c2")];
    const prior = view([turn({ turnIndex: 1, runId: "r1", status: "Success", focused: false, steps: held, stepCount: 2 })]);
    const delta = view([turn({ turnIndex: 1, runId: "r1", status: "Success", focused: false, steps: [], stepCount: 2 })]);

    expect(mergeJournalDelta(prior, delta)?.turns[0].steps.map((s) => s.id)).toEqual(["c1", "c2"]);
  });

  it("reports divergence when a step landed below the cursor", () => {
    // An out-of-order backfill cannot ride an append-only delta: the server counts 4, the client can only account for 3.
    // Returning null is the whole reason trimming is safe — the caller replaces its accumulation with a full fetch.
    const prior = view([turn({ steps: [step("s1"), step("s2")], stepCount: 2 })]);
    const delta = view([turn({ steps: [step("s3")], stepCount: 4 })]);

    expect(mergeJournalDelta(prior, delta)).toBeNull();
  });

  it("reports divergence when a non-focused turn finished between two polls", () => {
    // It was live at the snapshot (so its steps came inline) and terminal by this poll (so they were trimmed) — but it
    // gained steps in between that this client never received. Silently keeping the stale copy is what the count forbids.
    const prior = view([turn({ turnIndex: 1, runId: "r1", status: "Running", focused: false, steps: [step("c1")], stepCount: 1 })]);
    const delta = view([turn({ turnIndex: 1, runId: "r1", status: "Success", focused: false, steps: [], stepCount: 3 })]);

    expect(mergeJournalDelta(prior, delta)).toBeNull();
  });

  it("does not carry a prior attempt's steps onto a rerun of the same turn", () => {
    // A rerun keeps the turn index and takes a NEW run id. The failed attempt's walk is not the new attempt's, so it must
    // not be merged in; here that shows up as divergence (1 held + 1 arrived would over-count the server's 1).
    const prior = view([turn({ turnIndex: 1, runId: "r1", steps: [step("old")], stepCount: 1 })]);
    const delta = view([turn({ turnIndex: 1, runId: "r2", steps: [step("new")], stepCount: 1 })]);

    expect(mergeJournalDelta(prior, delta)?.turns[0].steps.map((s) => s.id)).toEqual(["new"]);
  });

  it("re-delivers a step that moved without duplicating it", () => {
    // An in-flight beat whose timestamp advances when it resolves comes back under the same id. Upserting by id replaces
    // the held copy and puts it where its new timestamp belongs — the end — so the count still agrees.
    const prior = view([turn({ steps: [step("a"), step("pending")], stepCount: 2 })]);
    const delta = view([turn({ steps: [step("pending")], stepCount: 2 })]);

    expect(mergeJournalDelta(prior, delta)?.turns[0].steps.map((s) => s.id)).toEqual(["a", "pending"]);
  });

  it("takes every non-step field from the delta, which is authoritative", () => {
    const prior = view([turn({ status: "Running", summary: null, steps: [step("s1")], stepCount: 1 })]);
    const delta = view([turn({ status: "Success", summary: "done", steps: [], stepCount: 1 })]);

    const merged = mergeJournalDelta(prior, delta);

    expect(merged?.turns[0].status).toBe("Success");
    expect(merged?.turns[0].summary).toBe("done");
    expect(merged?.turns[0].steps.map((s) => s.id)).toEqual(["s1"]);
  });

  it("accepts a turn the client has never seen when the delta carries it whole", () => {
    const prior = view([]);
    const delta = view([turn({ turnIndex: 2, runId: "r2", steps: [step("n1")], stepCount: 1 })]);

    expect(mergeJournalDelta(prior, delta)?.turns[0].steps.map((s) => s.id)).toEqual(["n1"]);
  });
});
