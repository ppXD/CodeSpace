import { describe, expect, it } from "vitest";

import { deriveSecretHint } from "./storageSecretHint";

const ossSchema = {
  type: "object",
  properties: {
    accessKeyId: { type: "string" },
    accessKeySecret: { type: "string", writeOnly: true },
    securityToken: { type: "string", writeOnly: true },
  },
  required: ["accessKeyId", "accessKeySecret"],
};

describe("deriveSecretHint", () => {
  it("masks the field the provider did not mark as write-only", () => {
    expect(deriveSecretHint(ossSchema, { accessKeyId: "LTAI5tExampleKeyId", accessKeySecret: "s" })).toBe("LTAI5tE…eyId");
  });

  it("shows a short value whole, since masking it would hide all of it", () => {
    expect(deriveSecretHint(ossSchema, { accessKeyId: "LTAI5t" })).toBe("LTAI5t");
  });

  // The load-bearing case. A schema that marks nothing write-only has told us nothing is safe to display, so echoing
  // its first field would write the secret itself into a value every screen shows.
  it("refuses to echo anything when the provider does not distinguish its secret fields", () => {
    const undistinguished = { type: "object", properties: { token: { type: "string" } }, required: ["token"] };

    expect(deriveSecretHint(undistinguished, { token: "super-secret-value" })).toBeNull();
  });

  it("never echoes a write-only field, even when it is the only one filled", () => {
    expect(deriveSecretHint(ossSchema, { accessKeySecret: "super-secret-value" })).toBeNull();
  });

  it("has nothing to say about a provider with no secret inputs", () => {
    expect(deriveSecretHint({ type: "object", properties: {} }, {})).toBeNull();
  });
});
