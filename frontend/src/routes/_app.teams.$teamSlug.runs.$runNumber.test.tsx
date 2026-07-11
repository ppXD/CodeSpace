import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";

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
