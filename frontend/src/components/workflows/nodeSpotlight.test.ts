import { describe, expect, it } from "vitest";

import type { NodeKind, NodeManifestDto } from "@/api/workflows";

import { resolveSpotlight } from "./nodeSpotlight";

/** A manifest carrying arbitrary config/input schemas — the only fields resolveSpotlight reads. */
const manifest = (configSchema: unknown, inputSchema: unknown = {}): NodeManifestDto => ({
  typeKey: "t", displayName: "T", category: "AI", kind: "Regular" as NodeKind, description: null, iconKey: null,
  configSchema, inputSchema, outputSchema: {},
});

describe("resolveSpotlight", () => {
  it("returns [] for a manifest with no x-spotlight anywhere", () => {
    const m = manifest({ properties: { model: { type: "string" }, temperature: { type: "number" } } });
    expect(resolveSpotlight(m, { model: "claude-code", temperature: 0.4 }, {})).toEqual([]);
  });

  it("orders chips by ascending rank and caps at 3", () => {
    const m = manifest({
      properties: {
        a: { type: "string", "x-spotlight": 3 },
        b: { type: "string", "x-spotlight": 1 },
        c: { type: "string", "x-spotlight": 2 },
        d: { type: "string", "x-spotlight": 4 },
      },
    });
    const chips = resolveSpotlight(m, { a: "aa", b: "bb", c: "cc", d: "dd" }, {});
    expect(chips.map((c) => c.key)).toEqual(["b", "c", "a"]);   // rank 1,2,3 — rank-4 "d" dropped by the ≤3 cap
  });

  it("merges spotlight props from BOTH config and input schemas, ranked together", () => {
    const m = manifest(
      { properties: { model: { type: "string", "x-spotlight": 2 } } },
      { properties: { prompt: { type: "string", "x-spotlight": 1 } } },
    );
    const chips = resolveSpotlight(m, { model: "opus" }, { prompt: "do it" });
    expect(chips.map((c) => c.key)).toEqual(["prompt", "model"]);
  });

  it("resolves the value from config first, then falls back to inputs", () => {
    const m = manifest({ properties: { x: { type: "string", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, {}, { x: "from-inputs" })[0].value).toBe("from-inputs");
    expect(resolveSpotlight(m, { x: "from-config" }, { x: "from-inputs" })[0].value).toBe("from-config");
  });

  it("renders a plain string value (neutral)", () => {
    const m = manifest({ properties: { harness: { type: "string", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, { harness: "claude-code" }, {})[0]).toMatchObject({ value: "claude-code", tone: "neutral" });
  });

  it("maps an enum value through x-enumLabels to its friendly label", () => {
    const m = manifest({
      properties: {
        autonomy: { type: "string", enum: ["low", "high"], "x-enumLabels": { low: "Cautious", high: "Unleashed" }, "x-spotlight": 1 },
      },
    });
    expect(resolveSpotlight(m, { autonomy: "high" }, {})[0]).toMatchObject({ value: "Unleashed", tone: "neutral" });
  });

  it("shows the property title when a boolean is true, and a muted 'off' when false", () => {
    const m = manifest({ properties: { stream: { type: "boolean", title: "Streaming", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, { stream: true }, {})[0]).toMatchObject({ value: "Streaming", tone: "neutral" });

    const off = resolveSpotlight(m, { stream: false }, {})[0];
    expect(off).toMatchObject({ value: "off", tone: "neutral", unset: true });
  });

  it("renders a number as its string", () => {
    const m = manifest({ properties: { maxTurns: { type: "number", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, { maxTurns: 12 }, {})[0]).toMatchObject({ value: "12", tone: "neutral" });
  });

  it("collapses an object value to a JSON type badge", () => {
    const m = manifest({ properties: { responseSchema: { type: "object", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, { responseSchema: { type: "object", properties: {} } }, {})[0]).toMatchObject({ value: "JSON", tone: "neutral" });
  });

  it("shortens a {{ref}} to its trailing dotted segment(s) as a mono chip", () => {
    const m = manifest({ properties: { items: { type: "string", "x-spotlight": 1 } } });
    const chip = resolveSpotlight(m, { items: "{{ nodes.plan.outputs.json.subtasks }}" }, {})[0];
    expect(chip.tone).toBe("mono");
    expect(chip.value.startsWith("← ")).toBe(true);
    expect(chip.value).toContain("subtasks");
    expect(chip.value.length).toBeLessThanOrEqual(24);   // "← " + ≤22 chars
  });

  it("shows a uuid (x-selector entity id) as a short # id, mono", () => {
    const m = manifest({ properties: { repositoryId: { type: "string", "x-selector": "repository", "x-spotlight": 1 } } });
    const chip = resolveSpotlight(m, { repositoryId: "a1b2c3d4-e5f6-7890-abcd-ef1234567890" }, {})[0];
    expect(chip).toMatchObject({ value: "#a1b2c3", tone: "mono" });
  });

  it("renders a muted placeholder (title, unset) when the property has no value", () => {
    const m = manifest({ properties: { schedule: { type: "string", title: "Cron schedule", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, {}, {})[0]).toMatchObject({ value: "Cron schedule", unset: true, tone: "neutral" });
  });

  it("uses the raw key as the unset placeholder when the property has no title", () => {
    const m = manifest({ properties: { schedule: { type: "string", "x-spotlight": 1 } } });
    expect(resolveSpotlight(m, { schedule: "" }, {})[0]).toMatchObject({ value: "schedule", unset: true });
  });

  it("middle-ellipsizes an overlong plain string to ≤ ~24 chars", () => {
    const m = manifest({ properties: { cmd: { type: "string", "x-spotlight": 1 } } });
    const chip = resolveSpotlight(m, { cmd: "a-really-quite-long-command-string-value" }, {})[0];
    expect(chip.value.length).toBeLessThanOrEqual(24);
    expect(chip.value).toContain("…");
  });
});
