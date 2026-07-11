import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";
import { validateRunsSearch } from "./_app.teams.$teamSlug.runs.index";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The Runs cockpit's status/time lens + history page are deep-linkable (?lens=&historyPage=) so a cockpit
 * view is shareable and Back/Forward work — the audit's PR10.
 */
describe("runs cockpit deep-link", () => {
  const acme: MeTeam = {
    id: "a", slug: "acme", name: "Acme", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };
  const me = { id: "u", email: "u@test.local", name: "U", teams: [acme], passwordMustChange: false } satisfies MeResponse;

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("carries ?lens= and ?historyPage= through the real router to the cockpit", async () => {
    stubFetch({
      "/api/workflows/runs/summary": { live: 0, failed: 0, suspended: 0, suspendedNeedingReview: 0, today: 0 },
      "/api/workflows/runs": [],
      "/api/users/me": me,
    });

    const { router } = await renderRoute("/teams/acme/runs?lens=failed&historyPage=2");

    await waitFor(() => expect(router.state.location.pathname).toBe("/teams/acme/runs"));
    expect(router.state.location.search).toMatchObject({ lens: "failed", historyPage: 2 });
  });

  describe("validateRunsSearch whitelist", () => {
    it("keeps a valid lens and a page > 1", () => {
      expect(validateRunsSearch({ lens: "failed", historyPage: 3 })).toEqual({ lens: "failed", historyPage: 3 });
    });

    it("drops an unknown lens and the default page (1)", () => {
      expect(validateRunsSearch({ lens: "bogus", historyPage: 1 })).toEqual({});
    });

    it("parses agentDefinitionIds from a single string or array", () => {
      expect(validateRunsSearch({ agentDefinitionIds: "agent-1" })).toEqual({ agentDefinitionIds: ["agent-1"] });
      expect(validateRunsSearch({ agentDefinitionIds: ["a", "", "b"] })).toEqual({ agentDefinitionIds: ["a", "b"] });
    });
  });
});
