import { describe, expect, it } from "vitest";

import type { CredentialSummary } from "@/api/types";

import { bindableCredentials, credentialOwnershipLabel, sortCredentialsByPreference } from "./credentialOrdering";

const cred = (id: string, displayName: string, ownership: "Personal" | "TeamService", ownerUserName?: string, ownerUserId?: string): CredentialSummary => ({
  id, teamId: "t", providerInstanceId: "pi", ownerUserId: ownerUserId ?? null, ownership, ownerUserName: ownerUserName ?? null,
  authType: "GroupAccessToken", displayName, status: "Active", createdDate: "",
});

describe("sortCredentialsByPreference", () => {
  it("puts team-service credentials first, then alphabetical", () => {
    const out = sortCredentialsByPreference([
      cred("1", "Bob's GitLab", "Personal", "Bob"),
      cred("2", "Acme team", "TeamService"),
      cred("3", "Alice's GitLab", "Personal", "Alice"),
    ]);
    // Team-service ("2") first; then personals alphabetically: "Alice's…" ("3") before "Bob's…" ("1").
    expect(out.map((c) => c.id)).toEqual(["2", "3", "1"]);
  });

  it("orders personal credentials alphabetically by display name", () => {
    const out = sortCredentialsByPreference([
      cred("a", "Zeta", "Personal"),
      cred("b", "Alpha", "Personal"),
    ]);
    expect(out.map((c) => c.displayName)).toEqual(["Alpha", "Zeta"]);
  });
});

describe("credentialOwnershipLabel", () => {
  it("labels a team-service credential", () => {
    expect(credentialOwnershipLabel(cred("1", "Acme", "TeamService"))).toBe("Team service");
  });

  it("labels a personal credential with the owner's name", () => {
    expect(credentialOwnershipLabel(cred("1", "x", "Personal", "Alice"))).toBe("Alice");
  });

  it("falls back to 'Personal' when a personal credential has no owner name", () => {
    expect(credentialOwnershipLabel(cred("1", "x", "Personal"))).toBe("Personal");
  });
});

describe("bindableCredentials", () => {
  it("keeps team-service + the current user's own personal, drops other people's personal", () => {
    const mine = cred("mine", "My GitLab", "Personal", "Me", "u1");
    const team = cred("team", "Acme team", "TeamService");          // owner-less
    const teammate = cred("bob", "Bob's GitLab", "Personal", "Bob", "u2");

    const out = bindableCredentials([mine, team, teammate], "u1");
    expect(out.map((c) => c.id)).toEqual(["mine", "team"]);
  });

  it("shows only team-service while the current user is unknown (still loading)", () => {
    const mine = cred("mine", "My GitLab", "Personal", "Me", "u1");
    const team = cred("team", "Acme team", "TeamService");

    expect(bindableCredentials([mine, team], undefined).map((c) => c.id)).toEqual(["team"]);
  });

  it("returns empty when only other people's personal credentials exist", () => {
    const teammate = cred("bob", "Bob's GitLab", "Personal", "Bob", "u2");
    expect(bindableCredentials([teammate], "u1")).toEqual([]);
  });
});
