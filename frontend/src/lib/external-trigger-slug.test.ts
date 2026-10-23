import { describe, expect, it } from "vitest";

import { deriveExternalTriggerSlug, isUsableExternalTriggerSlug } from "./external-trigger-slug";

describe("external trigger slug parity with backend golden vectors", () => {
  it.each([
    ["Prod-Alerts", "prod-alerts"],
    ["日本語", ""],
    ["prod 错误 报警", "prod"],
    ["../../etc/passwd", "etc-passwd"],
    ["  Padded  Name  ", "padded-name"],
    ["snake_case_kept", "snake_case_kept"],
    ["café münch", "caf-m-nch"],
    ["---spaces---", "spaces"],
    ["$$$", ""],
    ["a".repeat(63) + " b", "a".repeat(63)],
    ["A".repeat(80), "a".repeat(64)],
  ])("derives %s as %s", (input, expected) => {
    expect(deriveExternalTriggerSlug(input)).toBe(expected);
  });

  it.each(["", " ", "UPPER", "日本語", "a/b", "a".repeat(65)])("rejects unusable slug %s", (slug) => {
    expect(isUsableExternalTriggerSlug(slug)).toBe(false);
  });

  it.each(["prod-alerts", "snake_case", "0", "a".repeat(64)])("accepts %s", (slug) => {
    expect(isUsableExternalTriggerSlug(slug)).toBe(true);
  });
});
