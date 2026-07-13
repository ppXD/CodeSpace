import type { NodeManifestDto } from "@/api/workflows";

/**
 * A single spotlight chip on a node card — the resolved display of ONE high-ranked config/input param
 * (a manifest property flagged `"x-spotlight": N`, N∈1..3). Up to 3 render between the id ref line and
 * the "Writes / Waits" badges, so a viewer reads "what this step acts on / how much power" without
 * opening the inspector. Purely display: never written back.
 */
export interface SpotlightChip {
  /** Schema property path — the stable React key. */
  key: string;
  /** Prefix when the value alone is ambiguous (e.g. "until", "base"). Absent when the value speaks for itself. */
  label?: string;
  /** Resolved display value ("claude-code", "0 9 * * 1-5", "← plan…subtasks"). */
  value: string;
  tone: "neutral" | "tone" | "mono";
  /** Property has no value → render a muted "not set" placeholder. */
  unset?: boolean;
}

/** The minimal schema shape we read — each is `unknown` off the wire, so every access is typeof-guarded. */
interface SpotlightSchema {
  properties?: Record<string, SpotlightProp>;
}
interface SpotlightProp {
  "x-spotlight"?: unknown;
  "x-enumLabels"?: Record<string, string>;
  "x-selector"?: unknown;
  title?: unknown;
  type?: unknown;
  enum?: unknown;
}

const asObject = (v: unknown): Record<string, unknown> | undefined =>
  v && typeof v === "object" ? (v as Record<string, unknown>) : undefined;

const asSchema = (v: unknown): SpotlightSchema => (asObject(v) as SpotlightSchema) ?? {};

/** A candidate property flagged for the spotlight — its path, rank, and schema node. */
interface Candidate {
  key: string;
  rank: number;
  prop: SpotlightProp;
}

/** Walk one schema's top-level properties, collecting every prop that carries a numeric `x-spotlight`. */
function collect(schema: unknown, out: Candidate[]): void {
  const props = asSchema(schema).properties;
  if (!props || typeof props !== "object") return;

  for (const [key, raw] of Object.entries(props)) {
    const prop = asObject(raw) as SpotlightProp | undefined;
    if (!prop) continue;

    const rank = prop["x-spotlight"];
    if (typeof rank === "number") out.push({ key, rank, prop });
  }
}

const UUID_RE = /^[0-9a-f-]{36}$/i;

/** Middle-ellipsize to at most `max` chars ("a-very-long-thing" → "a-very…thing"). */
function ellipsize(s: string, max: number): string {
  if (s.length <= max) return s;
  const keep = max - 1;
  const head = Math.ceil(keep / 2);
  const tail = Math.floor(keep / 2);
  return `${s.slice(0, head)}…${s.slice(s.length - tail)}`;
}

/** The prop's human title (when a string) else the raw key — used for the boolean-true chip and the unset placeholder. */
function titleOf(key: string, prop: SpotlightProp): string {
  return typeof prop.title === "string" && prop.title.trim() !== "" ? prop.title : key;
}

/** A bound `{{ nodes.plan.outputs.json.subtasks }}` → a short trailing-segment mono chip ("← json.subtasks"). */
function refChip(key: string, raw: string): SpotlightChip {
  const inner = raw.match(/\{\{\s*(.+?)\s*\}\}/)?.[1] ?? raw;
  const segs = inner.split(".").filter(Boolean);
  // Keep the trailing 1-2 dotted segments, then bound the width.
  let tail = segs.slice(-2).join(".");
  if (tail.length > 22) tail = segs.slice(-1).join(".");
  return { key, value: `← ${ellipsize(tail, 22)}`, tone: "mono" };
}

/** Resolve one flagged property's live value into its display chip. */
function toChip(key: string, prop: SpotlightProp, value: unknown): SpotlightChip {
  if (value === undefined || value === null || value === "") {
    return { key, value: titleOf(key, prop), unset: true, tone: "neutral" };
  }

  if (typeof value === "string" && value.includes("{{")) return refChip(key, value);

  if (typeof value === "boolean") {
    return value
      ? { key, value: titleOf(key, prop), tone: "neutral" }
      : { key, value: "off", unset: true, tone: "neutral" };
  }

  if (typeof value === "number") return { key, value: String(value), tone: "neutral" };

  if (typeof value === "object") return { key, value: "JSON", tone: "neutral" };

  const str = String(value);

  // A uuid (typically an x-selector entity id) → honest short id; name resolution is a later polish.
  if (UUID_RE.test(str)) return { key, value: `#${str.slice(0, 6)}`, tone: "mono" };

  // An enum with friendly labels → the label for this value; else the raw value.
  if (Array.isArray(prop.enum) && prop["x-enumLabels"] && typeof prop["x-enumLabels"] === "object") {
    const label = prop["x-enumLabels"][str];
    if (typeof label === "string") return { key, value: ellipsize(label, 24), tone: "neutral" };
  }

  return { key, value: ellipsize(str, 24), tone: "neutral" };
}

/**
 * The up-to-3 spotlight chips for a node — the manifest's highest-ranked config/input params resolved
 * against the node's live config/inputs. A manifest with no `x-spotlight` anywhere returns `[]`, so a
 * node opts into the chips purely by its annotated manifest with no per-node UI. Pure → unit-tested;
 * precomputed in definitionToRfNodes so every canvas gets chips with no caller change.
 */
export function resolveSpotlight(manifest: NodeManifestDto, config: unknown, inputs: unknown): SpotlightChip[] {
  const candidates: Candidate[] = [];
  collect(manifest.configSchema, candidates);
  collect(manifest.inputSchema, candidates);

  if (candidates.length === 0) return [];

  const cfg = asObject(config);
  const inp = asObject(inputs);

  return candidates
    .sort((a, b) => a.rank - b.rank)
    .slice(0, 3)
    .map(({ key, prop }) => toChip(key, prop, cfg?.[key] ?? inp?.[key]));
}
