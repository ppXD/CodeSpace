import { describe, expect, it } from "vitest";

import { migrateLegacyTriggerConfig, normaliseTriggerConfigForSave } from "./migrateTriggerConfig";

/**
 * The migration helper sits between the on-disk activation config (which may be one of
 * three shapes: legacy single-repo, new array, or empty/corrupt) and the trigger
 * inspector UI (which needs exactly one shape: the array). It MUST never throw and
 * MUST never silently drop data the operator put there.
 */
describe("migrateLegacyTriggerConfig", () => {
  it("returns empty repositories for {}", () => {
    expect(migrateLegacyTriggerConfig({})).toEqual({ repositories: [] });
  });

  it("returns empty repositories for null", () => {
    expect(migrateLegacyTriggerConfig(null)).toEqual({ repositories: [] });
  });

  it("returns empty repositories for undefined", () => {
    expect(migrateLegacyTriggerConfig(undefined)).toEqual({ repositories: [] });
  });

  it("returns empty repositories for non-object inputs (defensive — never throws)", () => {
    // Corrupt config row, double-stringified JSON, raw number — all flow through here.
    expect(migrateLegacyTriggerConfig("not-an-object")).toEqual({ repositories: [] });
    expect(migrateLegacyTriggerConfig(42)).toEqual({ repositories: [] });
    expect(migrateLegacyTriggerConfig(true)).toEqual({ repositories: [] });
    expect(migrateLegacyTriggerConfig([])).toEqual({ repositories: [] });
  });

  it("promotes legacy { repositoryId } to a one-entry list with no labels key", () => {
    // No labels in legacy config ⇒ no labels in the migrated entry. Keeps the wire
    // shape minimal so a diff after the auto-migration is "shape change, no semantic
    // change" — easy to review.
    expect(migrateLegacyTriggerConfig({ repositoryId: "repo-1" })).toEqual({
      repositories: [{ repositoryId: "repo-1" }],
    });
  });

  it("promotes legacy { repositoryId, labels } preserving labels", () => {
    expect(migrateLegacyTriggerConfig({ repositoryId: "repo-1", labels: ["bug", "wip"] })).toEqual({
      repositories: [{ repositoryId: "repo-1", labels: ["bug", "wip"] }],
    });
  });

  it("legacy { repositoryId, labels: [] } promotes WITHOUT a labels key", () => {
    // Empty labels in legacy = no label filter. We strip the empty array so the
    // migrated shape stays minimal and matches what a freshly-created entry produces.
    expect(migrateLegacyTriggerConfig({ repositoryId: "repo-1", labels: [] })).toEqual({
      repositories: [{ repositoryId: "repo-1" }],
    });
  });

  it("passes through the already-new repositories shape", () => {
    const already = { repositories: [{ repositoryId: "a", labels: ["x"] }, { repositoryId: "b" }] };
    expect(migrateLegacyTriggerConfig(already)).toEqual(already);
  });

  it("new shape with malformed entries skips them, keeps the good ones AND in-progress empty entries", () => {
    // The picker uses migrate on every render — an empty-repositoryId row represents
    // "user clicked Add but hasn't picked yet" and MUST survive the render. Truly
    // malformed shapes (no repositoryId key, wrong type) ARE dropped.
    const mixed = {
      repositories: [
        { repositoryId: "good-1", labels: ["a"] },
        { repositoryId: "" },             // in-progress — KEPT
        { labels: ["orphan"] },           // no id key — dropped
        "string-not-object",              // wrong type — dropped
        null,                              // wrong type — dropped
        { repositoryId: "good-2" },
      ],
    };
    expect(migrateLegacyTriggerConfig(mixed)).toEqual({
      repositories: [
        { repositoryId: "good-1", labels: ["a"] },
        { repositoryId: "" },
        { repositoryId: "good-2" },
      ],
    });
  });

  it("when both new and legacy keys present, new wins (matches matcher precedence)", () => {
    // PrTriggerMatcherFilter (PR #23) treats `repositories` as authoritative when
    // present. The migration helper mirrors that precedence so the UI shows the
    // operator the same set the backend would match against.
    const ambiguous = { repositories: [{ repositoryId: "from-new" }], repositoryId: "from-legacy" };
    expect(migrateLegacyTriggerConfig(ambiguous)).toEqual({ repositories: [{ repositoryId: "from-new" }] });
  });

  it("filters out non-string labels (defensive — null/number/object entries skipped)", () => {
    const malformed = { repositoryId: "r", labels: ["ok", null, 42, { x: 1 }, "", "also-ok"] };
    expect(migrateLegacyTriggerConfig(malformed)).toEqual({
      repositories: [{ repositoryId: "r", labels: ["ok", "also-ok"] }],
    });
  });
});

describe("normaliseTriggerConfigForSave", () => {
  it("drops entries with empty repositoryId", () => {
    const dirty = { repositories: [{ repositoryId: "" }, { repositoryId: "keep" }] };
    expect(normaliseTriggerConfigForSave(dirty)).toEqual({ repositories: [{ repositoryId: "keep" }] });
  });

  it("strips empty labels arrays (absent ≡ empty in the matcher; absent diffs cleaner)", () => {
    const withEmpty = { repositories: [{ repositoryId: "r", labels: [] }] };
    expect(normaliseTriggerConfigForSave(withEmpty)).toEqual({ repositories: [{ repositoryId: "r" }] });
  });

  it("keeps non-empty labels arrays verbatim", () => {
    const withLabels = { repositories: [{ repositoryId: "r", labels: ["a", "b"] }] };
    expect(normaliseTriggerConfigForSave(withLabels)).toEqual({ repositories: [{ repositoryId: "r", labels: ["a", "b"] }] });
  });
});
