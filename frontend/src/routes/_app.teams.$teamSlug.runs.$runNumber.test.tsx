import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";
import { validateRunDetailSearch } from "./_app.teams.$teamSlug.runs.$runNumber";

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
  });
});
