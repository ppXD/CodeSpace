import { describe, expect, it } from "vitest";

import { credentialForRef, parseCredentialRef } from "./storageCredentialRef";

describe("parseCredentialRef", () => {
  it("reads the key identity and the exact version it names", () => {
    expect(parseCredentialRef("db:2f1a-4c:7")).toEqual({ credentialId: "2f1a-4c", revision: 7 });
  });

  // A pointer this does not understand must not resolve to SOME key. Showing the wrong key beside a destination is
  // worse than showing none: it is what an operator would replace when the destination stops working.
  it.each([
    ["a store type this build cannot resolve", "vault:2f1a-4c:7"],
    ["no version at all", "db:2f1a-4c"],
    ["a version that is not a number", "db:2f1a-4c:latest"],
    ["a zero version", "db:2f1a-4c:0"],
    ["an empty identity", "db::3"],
    ["nothing", null],
  ])("refuses %s", (_reason, value) => {
    expect(parseCredentialRef(value as string | null)).toBeNull();
  });
});

describe("credentialForRef", () => {
  const keys = [{ id: "a" }, { id: "b" }];

  it("finds the key the pointer names rather than the first one of its kind", () => {
    expect(credentialForRef("db:b:2", keys)).toEqual({ id: "b" });
  });

  it("finds nothing when the named key is not in the list", () => {
    expect(credentialForRef("db:c:1", keys)).toBeUndefined();
  });
});
