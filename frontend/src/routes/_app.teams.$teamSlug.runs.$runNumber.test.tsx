import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { RoomBlock } from "@/api/sessions";
import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";
import { rewriteTraceDeepLink, validateRunDetailSearch } from "./_app.teams.$teamSlug.runs.$runNumber";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The run-detail page resolves the URL ref (a team-scoped run number, or a legacy GUID) and
 * canonicalises a legacy-GUID URL to the number URL. This is the redirect half of the run
 * GUID-killer (PR #1102) — proven here against the real router so a broken comparison (number
 * vs string) or a dropped `replace` surfaces as a failing redirect.
 */
describe("run-detail canonical redirect", () => {
  const acme: MeTeam = {
    id: "a", slug: "acme", name: "Acme", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };
  const me = { id: "u", email: "u@test.local", name: "U", teams: [acme], passwordMustChange: false } satisfies MeResponse;

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("redirects a legacy-GUID run URL to the clean /runs/{number} URL", async () => {
    // /room + /journal 404 (not under test); getRun resolves the ref to a run whose number is 42.
    stubFetch({
      "/room": undefined,
      "/journal": undefined,
      "/api/workflows/runs/": { id: "run-guid-1", runNumber: 42 },
      "/api/users/me": me,
    });

    const { currentPath } = await renderRoute("/teams/acme/runs/11111111-1111-1111-1111-111111111111");

    await waitFor(() => expect(currentPath()).toBe("/teams/acme/runs/42"));
  });

  it("does not redirect when the URL already carries the run number", async () => {
    stubFetch({
      "/room": undefined,
      "/journal": undefined,
      "/api/workflows/runs/": { id: "run-guid-1", runNumber: 42 },
      "/api/users/me": me,
    });

    const { currentPath } = await renderRoute("/teams/acme/runs/42");

    // Give any stray redirect a chance to fire, then assert the URL is unchanged.
    await new Promise((r) => setTimeout(r, 50));
    expect(currentPath()).toBe("/teams/acme/runs/42");
  });
});

/**
 * The raw-trace modal is deep-linkable: `?trace=` names the open run and `?view=` its inner tab, so a trace
 * view is shareable and Back closes it (the audit's run-trace deep-link). Proven against the real router.
 */
describe("run-detail trace deep-link", () => {
  const acme: MeTeam = {
    id: "a", slug: "acme", name: "Acme", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };
  const me = { id: "u", email: "u@test.local", name: "U", teams: [acme], passwordMustChange: false } satisfies MeResponse;

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("carries ?view= through the real router to the run-detail route", async () => {
    stubFetch({
      "/room": undefined,       // 404 → session-less run → the raw-trace modal IS the page
      "/journal": undefined,
      "/api/workflows/runs/": { id: "run-guid-1", runNumber: 42 },
      "/api/users/me": me,
    });

    const { router } = await renderRoute("/teams/acme/runs/42?view=canvas");

    await waitFor(() => expect(router.state.location.pathname).toBe("/teams/acme/runs/42"));
    expect(router.state.location.search).toMatchObject({ view: "canvas" });
  });

  describe("validateRunDetailSearch whitelist", () => {
    it("keeps a valid view and a non-empty trace", () => {
      expect(validateRunDetailSearch({ view: "canvas", trace: "run-1" })).toEqual({ trace: "run-1", view: "canvas" });
    });

    it("drops an unknown view and an empty trace", () => {
      expect(validateRunDetailSearch({ view: "bogus", trace: "" })).toEqual({});
    });

    it("keeps a lone trace (the default view is omitted for a clean URL)", () => {
      expect(validateRunDetailSearch({ trace: "run-1" })).toEqual({ trace: "run-1" });
    });

    it("keeps the companion-pane params, alongside the modal ones", () => {
      expect(validateRunDetailSearch({ pane: "canvas", turn: 3 })).toEqual({ pane: "canvas", turn: 3 });
      expect(validateRunDetailSearch({ pane: "canvas", turn: "3" })).toEqual({ pane: "canvas", turn: 3 });
      expect(validateRunDetailSearch({ trace: "run-1", pane: "canvas", turn: 2 })).toEqual({ trace: "run-1", pane: "canvas", turn: 2 });
    });

    it("keeps the widened pane mini-tabs (changes / trace), not just canvas (D5)", () => {
      expect(validateRunDetailSearch({ pane: "changes", turn: 3 })).toEqual({ pane: "changes", turn: 3 });
      expect(validateRunDetailSearch({ pane: "trace", turn: "5" })).toEqual({ pane: "trace", turn: 5 });
    });

    it("keeps a lone pane as FOLLOW mode (no turn), and drops an invalid turn to follow (D2)", () => {
      expect(validateRunDetailSearch({ pane: "canvas" })).toEqual({ pane: "canvas" });          // follow: pane alone
      expect(validateRunDetailSearch({ pane: "trace" })).toEqual({ pane: "trace" });            // follow: any mini-tab
      expect(validateRunDetailSearch({ pane: "canvas", turn: 0 })).toEqual({ pane: "canvas" }); // non-positive turn → follow
    });

    it("drops pane when the pane value is missing/unknown (no dangling half-param)", () => {
      expect(validateRunDetailSearch({ turn: 3 })).toEqual({});                  // pane missing → turn drops too
      expect(validateRunDetailSearch({ pane: "bogus", turn: 3 })).toEqual({});   // unknown pane
      expect(validateRunDetailSearch({ pane: "activity", turn: 3 })).toEqual({});// a modal-only view isn't a pane view
    });

    it("keeps the D3 canvas-focus `node` alongside a valid pane, and drops it without one (D3)", () => {
      expect(validateRunDetailSearch({ pane: "canvas", turn: 2, node: "map-1" })).toEqual({ pane: "canvas", turn: 2, node: "map-1" });
      expect(validateRunDetailSearch({ pane: "canvas", node: "map-1" })).toEqual({ pane: "canvas", node: "map-1" }); // node without a pin (follow-mode focus)
      expect(validateRunDetailSearch({ node: "map-1" })).toEqual({});                          // node with no pane → dropped
      expect(validateRunDetailSearch({ pane: "canvas", node: "" })).toEqual({ pane: "canvas" }); // empty node → dropped
    });
  });
});

/**
 * D4 decommissioned the run-trace modal in favor of the companion pane. A legacy `?trace=`/`?view=` deep-link is
 * one-time rewritten so it never 404s: THIS run's inner view maps to a pane mini-tab (activity → the journal, no pane);
 * a SUB / sibling run navigates to its own page. This pins the whole redirect matrix on the pure decision function.
 */
describe("rewriteTraceDeepLink — legacy trace deep-link → companion pane", () => {
  // One turn in the room: turn 3 IS this run. The pinned pane resolves its turn by matching runId in the blocks.
  const blocks = [{ type: "assistant_turn", id: "t3", turnIndex: 3, runId: "this-run" } as unknown as RoomBlock];

  it("is a no-op when there's no legacy ?trace", () => {
    expect(rewriteTraceDeepLink(undefined, "canvas", "this-run", blocks)).toEqual({ kind: "none" });
  });

  it("maps THIS run's canvas / changes / trace views to the matching pinned mini-tab", () => {
    expect(rewriteTraceDeepLink("this-run", "canvas", "this-run", blocks)).toEqual({ kind: "pane", pane: "canvas", turn: 3 });
    expect(rewriteTraceDeepLink("this-run", "changes", "this-run", blocks)).toEqual({ kind: "pane", pane: "changes", turn: 3 });
    expect(rewriteTraceDeepLink("this-run", "trace", "this-run", blocks)).toEqual({ kind: "pane", pane: "trace", turn: 3 });
  });

  it("treats a missing ?view as the modal's default tab (trace)", () => {
    expect(rewriteTraceDeepLink("this-run", undefined, "this-run", blocks)).toEqual({ kind: "pane", pane: "trace", turn: 3 });
  });

  it("drops THIS run's activity view to the journal (no pane)", () => {
    expect(rewriteTraceDeepLink("this-run", "activity", "this-run", blocks)).toEqual({ kind: "clear" });
  });

  it("navigates a SUB / sibling run to its own page", () => {
    expect(rewriteTraceDeepLink("other-run", "trace", "this-run", blocks)).toEqual({ kind: "subrun", runId: "other-run" });
    expect(rewriteTraceDeepLink("other-run", "canvas", "this-run", blocks)).toEqual({ kind: "subrun", runId: "other-run" });
  });

  it("holds (pending) until the room resolves this run's turn", () => {
    expect(rewriteTraceDeepLink("this-run", "canvas", "this-run", undefined)).toEqual({ kind: "pending" });
  });

  it("opens the pane in follow mode (turn omitted) when no turn matches this run", () => {
    const other = [{ type: "assistant_turn", id: "t1", turnIndex: 1, runId: "someone-else" } as unknown as RoomBlock];
    expect(rewriteTraceDeepLink("this-run", "canvas", "this-run", other)).toEqual({ kind: "pane", pane: "canvas" });
  });
});
