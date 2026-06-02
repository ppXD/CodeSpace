import { describe, expect, it } from "vitest";

import { ApiError } from "@/api/request";

import { parseActorIdentityRequired } from "./actorIdentity";

describe("parseActorIdentityRequired", () => {
  it("parses a 428 actor_identity_required ApiError into the link target", () => {
    const err = new ApiError(428, "actor_identity_required", "link required", {
      code: "actor_identity_required", message: "link required", provider: "GitHub", providerInstanceId: "inst-1",
    });

    expect(parseActorIdentityRequired(err)).toEqual({ providerInstanceId: "inst-1", provider: "GitHub", message: "link required" });
  });

  it("returns null for a different error code", () => {
    expect(parseActorIdentityRequired(new ApiError(403, "forbidden", "no"))).toBeNull();
  });

  it("returns null for non-ApiError values", () => {
    expect(parseActorIdentityRequired(new Error("boom"))).toBeNull();
    expect(parseActorIdentityRequired(null)).toBeNull();
    expect(parseActorIdentityRequired(undefined)).toBeNull();
  });

  it("returns null when the body omits providerInstanceId (the modal can't target an instance)", () => {
    const err = new ApiError(428, "actor_identity_required", "link required", { provider: "GitHub" });

    expect(parseActorIdentityRequired(err)).toBeNull();
  });

  it("falls back to a generic label + the error message when optional body fields are absent", () => {
    const err = new ApiError(428, "actor_identity_required", "fallback msg", { providerInstanceId: "inst-2" });

    expect(parseActorIdentityRequired(err)).toEqual({ providerInstanceId: "inst-2", provider: "your provider", message: "fallback msg" });
  });
});
