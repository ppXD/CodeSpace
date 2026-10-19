import { describe, expect, it } from "vitest";

import type { CredentialSummary, RepositorySummary } from "@/api/types";
import { canMoveBindingsOff, credentialHealthWord, dependentsLabel, isHistory, isLiveCredential, isStaticToken, repositoryConnectionIssue } from "./credentialHealth";

const credential = (over: Partial<CredentialSummary> = {}): CredentialSummary => ({
  id: "c1", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownership: "Personal", authType: "OAuth", displayName: "GitLab · nacho", status: "Active", createdDate: "2026-01-01T00:00:00Z", ...over,
});

const repository = (over: Partial<Pick<RepositorySummary, "credentialId" | "status" | "lastError">> = {}) => ({ credentialId: "c1" as string | null, status: "Active" as RepositorySummary["status"], lastError: null as string | null, ...over });

const byId = (...credentials: CredentialSummary[]) => new Map(credentials.map((c) => [c.id, c] as const));

describe("credentialHealthWord", () => {
  it.each([
    [{ status: "Active" }, "active"],
    [{ status: "Active", health: "active" }, "active"],
    [{ status: "Active", health: "expiring" }, "expiring"],
    [{ status: "Active", health: "rejected" }, "rejected"],       // a token whose recorded expiry passed reads rejected before anything has marked the row
    [{ status: "Error" }, "rejected"],                            // a server that sends no health: status is what there is
    [{ status: "Expired" }, "rejected"],
    [{ status: "Revoked" }, "disconnected"],
    [{ status: "Active", health: "revoked" }, "disconnected"],
    [{ status: "Active", health: "owner_left" }, "owner_left"],
    [{ status: "Revoked", health: "owner_left" }, "disconnected"],   // disconnected beats everything: nothing is left to be healthy
  ] as const)("reads %j as %s", (over, word) => {
    expect(credentialHealthWord(credential(over as Partial<CredentialSummary>))).toBe(word);
  });

  it("calls only an active or expiring credential live", () => {
    expect(isLiveCredential(credential())).toBe(true);
    expect(isLiveCredential(credential({ health: "expiring" }))).toBe(true);
    for (const over of [{ status: "Error" }, { status: "Revoked" }, { health: "rejected" }, { health: "owner_left" }] as const) expect(isLiveCredential(credential(over as Partial<CredentialSummary>))).toBe(false);
  });

  it("tells a pasted token from a sign-in", () => {
    expect(isStaticToken({ authType: "Pat" })).toBe(true);
    expect(isStaticToken({ authType: "ProjectAccessToken" })).toBe(true);
    expect(isStaticToken({ authType: "GroupAccessToken" })).toBe(true);
    expect(isStaticToken({ authType: "OAuth" })).toBe(false);
  });
});

describe("isHistory — a disconnected credential nothing leans on", () => {
  it("is history only when the server says nothing leans on a disconnected credential", () => {
    expect(isHistory(credential({ status: "Revoked", dependentCount: 0 }))).toBe(true);
    expect(isHistory(credential({ status: "Active", health: "revoked", dependentCount: 0 }))).toBe(true);
  });

  it.each([
    ["something leans on it", { status: "Revoked", dependentCount: 1 }],
    ["an older server says nothing about what leans on it", { status: "Revoked" }],
    ["a server that sends no count", { status: "Revoked", dependentCount: null }],
    ["it is still connected", { status: "Active", dependentCount: 0 }],
    ["it was refused, not disconnected", { status: "Error", dependentCount: 0 }],
    ["its owner left", { status: "Active", health: "owner_left", dependentCount: 0 }],
  ] as const)("is not history when %s", (_why, over) => {
    expect(isHistory(credential(over as Partial<CredentialSummary>))).toBe(false);
  });
});

describe("canMoveBindingsOff — the credentials relink-bindings accepts as a source", () => {
  it.each([
    ["a disconnected credential with repositories", { status: "Revoked", boundRepositoryCount: 3 }, true],
    ["a refused credential with repositories", { status: "Error", boundRepositoryCount: 1 }, true],
    ["an expired credential with repositories", { status: "Expired", boundRepositoryCount: 2 }, true],
    ["a disconnected credential held only by a group hook: relink-bindings moves hooks with no repository to carry them", { status: "Revoked", boundRepositoryCount: 0, boundConnectionWebhookCount: 1 }, true],
    ["a refused credential held only by a group hook", { status: "Error", boundConnectionWebhookCount: 2 }, true],
    ["a disconnected credential held only by an act-as identity, which relink-bindings does not move", { status: "Revoked", boundRepositoryCount: 0, actAsIdentityCount: 1 }, false],
    ["a disconnected credential with none", { status: "Revoked", boundRepositoryCount: 0 }, false],
    ["a server that sends no count", { status: "Revoked" }, false],
    ["a credential in service held by a group hook", { status: "Active", boundConnectionWebhookCount: 2 }, false],
    ["a credential in service, however it reads", { status: "Active", health: "rejected", boundRepositoryCount: 4 }, false],
    ["a working credential", { status: "Active", boundRepositoryCount: 4 }, false],
    ["an expiring token", { status: "Active", health: "expiring", boundRepositoryCount: 4 }, false],
  ] as const)("says %s: %s", (_what, over, expected) => {
    expect(canMoveBindingsOff(credential(over as Partial<CredentialSummary>))).toBe(expected);
  });
});

describe("dependentsLabel", () => {
  it.each([
    [{ status: "Active", boundRepositoryCount: 1 }, "1 repository bound"],
    [{ status: "Active", boundRepositoryCount: 3 }, "3 repositories bound"],
    [{ status: "Active", health: "expiring", boundRepositoryCount: 2 }, "2 repositories bound"],
    [{ status: "Active", boundRepositoryCount: 2, boundConnectionWebhookCount: 3 }, "2 repositories bound"],   // a working credential says what a reconnect puts at stake: its repositories
    [{ status: "Revoked", boundRepositoryCount: 1 }, "1 repository still uses it"],
    [{ status: "Revoked", boundRepositoryCount: 3 }, "3 repositories still use it"],
    [{ status: "Error", boundRepositoryCount: 5 }, "5 repositories still use it"],
    [{ status: "Active", health: "rejected", boundRepositoryCount: 2 }, "2 repositories still use it"],
    [{ status: "Revoked", boundRepositoryCount: 0, boundConnectionWebhookCount: 1 }, "1 group hook still uses it"],
    [{ status: "Revoked", boundRepositoryCount: 0, boundConnectionWebhookCount: 4 }, "4 group hooks still use it"],
    [{ status: "Revoked", boundRepositoryCount: 0, actAsIdentityCount: 1 }, "1 act-as identity still uses it"],
    [{ status: "Revoked", boundRepositoryCount: 2, boundConnectionWebhookCount: 1 }, "2 repositories and 1 group hook still use it"],
    [{ status: "Error", boundRepositoryCount: 1, boundConnectionWebhookCount: 2, actAsIdentityCount: 3 }, "1 repository, 2 group hooks and 3 act-as identities still use it"],
  ] as const)("reads %j as '%s'", (over, label) => {
    expect(dependentsLabel(credential(over as Partial<CredentialSummary>))).toBe(label);
  });

  it("says nothing when nothing leans on it, and reads an older server's missing counts as none", () => {
    expect(dependentsLabel(credential({ status: "Revoked", boundRepositoryCount: 0 }))).toBeNull();
    expect(dependentsLabel(credential({ status: "Revoked" }))).toBeNull();
    expect(dependentsLabel(credential({ status: "Active", boundRepositoryCount: 0, boundConnectionWebhookCount: 2 }))).toBeNull();
  });
});

describe("repositoryConnectionIssue — joined by credentialId, not only status Error", () => {
  it("is nothing for a repository whose credential works", () => {
    expect(repositoryConnectionIssue(repository(), byId(credential()), true)).toBeNull();
  });

  it("flags a repository still marked Active whose credential was refused, with the right words for a sign-in and for a token", () => {
    expect(repositoryConnectionIssue(repository(), byId(credential({ status: "Error" })), true)).toMatchObject({ kind: "rejected", label: "needs reconnect" });
    expect(repositoryConnectionIssue(repository(), byId(credential({ status: "Error", authType: "Pat" })), true)).toMatchObject({ kind: "rejected", label: "needs a new token" });
  });

  it("flags a repository whose credential was disconnected", () => {
    expect(repositoryConnectionIssue(repository(), byId(credential({ status: "Revoked" })), true)).toMatchObject({ kind: "disconnected", label: "credential disconnected" });
  });

  it("flags a repository whose credential's person left the team", () => {
    expect(repositoryConnectionIssue(repository(), byId(credential({ health: "owner_left" })), true)).toMatchObject({ kind: "owner_left", label: "owner left" });
  });

  it("treats a credential the list no longer holds — retired, soft-deleted — as disconnected, once the list has arrived", () => {
    expect(repositoryConnectionIssue(repository(), byId(), true)).toMatchObject({ kind: "retired", label: "credential disconnected" });
  });

  it("says nothing about a missing credential until the list has loaded: absent from a list not yet read is not absent", () => {
    expect(repositoryConnectionIssue(repository(), byId(), false)).toBeNull();
    expect(repositoryConnectionIssue(repository({ status: "Error", lastError: "boom" }), byId(), false)).toMatchObject({ kind: "error", title: "boom" });
  });

  it("keeps the old reading for a repository in Error whose credential looks fine, or that has none", () => {
    expect(repositoryConnectionIssue(repository({ status: "Error" }), byId(credential()), true)).toMatchObject({ kind: "error", label: "needs new credential" });
    expect(repositoryConnectionIssue(repository({ status: "Error", credentialId: null }), byId(), true)).toMatchObject({ kind: "unbound", label: "needs new credential" });
    expect(repositoryConnectionIssue(repository({ credentialId: null }), byId(), true)).toBeNull();   // never bound is not broken
  });

  it("leaves an archived (paused) repository alone", () => {
    expect(repositoryConnectionIssue(repository({ status: "Paused" }), byId(credential({ status: "Revoked" })), true)).toBeNull();
  });

  it("hands back the credential the row runs through, so the fix can act on it", () => {
    const dead = credential({ status: "Error" });

    expect(repositoryConnectionIssue(repository(), byId(dead), true)?.credential).toBe(dead);
  });

  it("never carries a token into the words it says", () => {
    const issue = repositoryConnectionIssue(repository(), byId(credential({ status: "Error", lastError: "needs reconnect: invalid_grant" })), true)!;

    expect(`${issue.label} ${issue.title}`).not.toMatch(/glpat|Bearer|access_token/i);
  });
});
