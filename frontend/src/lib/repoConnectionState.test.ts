import { describe, expect, it } from "vitest";

import type { RepositorySummary } from "@/api/types";

import { repoConnectionState } from "./repoConnectionState";

const repo = (fullPath: string, projectIds: string[]): RepositorySummary => ({
  id: `id-${fullPath}`, teamId: "t", providerInstanceId: "pi", credentialId: "cred-alice",
  fullPath, name: fullPath, defaultBranch: "main", visibility: "Private", status: "Active",
  webUrl: "", createdDate: "", projects: projectIds.map((id) => ({ id, slug: id, name: id })),
});

const connected = new Map<string, RepositorySummary>([
  ["acme/in-p1", repo("acme/in-p1", ["p1"])],
  ["acme/in-p2", repo("acme/in-p2", ["p2"])],
]);

describe("repoConnectionState", () => {
  it("is fresh when the repo isn't connected to the team", () => {
    expect(repoConnectionState("acme/brand-new", connected, "p1")).toEqual({ state: "fresh" });
  });

  it("is in-project when the connected repo is already linked to the target project", () => {
    const result = repoConnectionState("acme/in-p1", connected, "p1");
    expect(result.state).toBe("in-project");
  });

  it("is connected-elsewhere when connected to the team but not the target project", () => {
    const result = repoConnectionState("acme/in-p2", connected, "p1");
    expect(result.state).toBe("connected-elsewhere");
    expect(result.state === "connected-elsewhere" && result.repo.credentialId).toBe("cred-alice");
  });

  it("treats a connected repo as connected-elsewhere when no target project is given", () => {
    expect(repoConnectionState("acme/in-p1", connected, undefined).state).toBe("connected-elsewhere");
  });
});
