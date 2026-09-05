import { afterEach, describe, expect, it, vi } from "vitest";

import { sessionsApi } from "./sessions";

/**
 * The wire shape of a human's verdict on a supervisor gate card (C4). Every gate surface sends the STRUCTURED
 * `decision` field so the backend rules on it instead of matching the leading word of the operator's free text —
 * the bug this replaces read a 繁中「批准」as revision feedback. A CONTENT question has no verdict to give, so
 * it must keep posting the bare `{ answer }` body an older server also accepts.
 */
function stubFetch() {
  const calls: Array<{ path: string; body: unknown }> = [];

  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ path: new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname, body: JSON.parse(String(init.body ?? "{}")) });
    return new Response(JSON.stringify({ resumed: true }), { status: 200, headers: { "content-type": "application/json" } });
  }));

  return calls;
}

afterEach(() => vi.unstubAllGlobals());

describe("answerRunAsk", () => {
  it("sends the structured verdict for a gate card, so the approval is a field and not a word", async () => {
    const calls = stubFetch();

    await sessionsApi.answerRunAsk("run-1", "批准，照這個計劃做", "approve");

    expect(calls[0].path).toBe("/api/workflows/runs/run-1/ask/answer");
    expect(calls[0].body).toEqual({ answer: "批准，照這個計劃做", decision: "approve" });
  });

  it("sends revise for typed guidance on an escalation, so text that starts with 'approve' cannot release the gate", async () => {
    const calls = stubFetch();

    await sessionsApi.answerRunAsk("run-1", "approve nothing until the tests pass", "revise");

    expect(calls[0].body).toEqual({ answer: "approve nothing until the tests pass", decision: "revise" });
  });

  it("omits the field entirely for a content question, which has no verdict to give", async () => {
    const calls = stubFetch();

    await sessionsApi.answerRunAsk("run-1", "use the staging Postgres");

    expect(calls[0].body).toEqual({ answer: "use the staging Postgres" });
    expect(calls[0].body).not.toHaveProperty("decision");
  });

  it("trims the answer before sending, leaving the verdict untouched", async () => {
    const calls = stubFetch();

    await sessionsApi.answerRunAsk("run-1", "  approve  ".trim(), "approve");

    expect(calls[0].body).toEqual({ answer: "approve", decision: "approve" });
  });
});

describe("confirmRunPlan", () => {
  it("posts the operator's click as the structured approve/feedback pair the confirm endpoint binds", async () => {
    const calls = stubFetch();

    await sessionsApi.confirmRunPlan("run-2", { approve: false, feedback: "merge the steps into one" });

    expect(calls[0].path).toBe("/api/workflows/runs/run-2/plan/confirm");
    expect(calls[0].body).toEqual({ approve: false, feedback: "merge the steps into one" });
  });
});
