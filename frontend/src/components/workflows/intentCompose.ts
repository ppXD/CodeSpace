/**
 * IntentLine composer — turns a node's `x-intent` template + its live config/inputs into a plain-language
 * sentence describing what the node WILL DO. Pure and resolver-injected so it is fully unit-testable.
 *
 * The template is a plain sentence on the node's ConfigSchema root (`x-intent`), with single-brace tokens:
 *   - `{path}`        substitute the value at `path` (dotted, resolved against merged {config, inputs}).
 *                     An entity id (a field carrying `x-selector`) renders as a friendly NAME, never a GUID;
 *                     a bound `{{ref}}` renders as a subtle chip; an unset field renders as a muted prompt.
 *   - `{flag?text}`   emit literal `text` iff the boolean at `flag` is truthy (the only conditional; no else).
 * `x-intentPlaceholders` (optional, on the same root) maps a path → the muted "choose …" prompt when unset.
 *
 * Display-only: the composer never writes config/inputs. Returns null when no `x-intent` is declared, so a
 * node opts in with a single additive schema string and every other node stays quiet.
 */

export type IntentSegment =
  | { type: "text"; text: string }
  | { type: "entity"; name: string; kind: string }
  | { type: "ref"; label: string }
  | { type: "prompt"; text: string };

export type EntityResolution =
  | { status: "resolved"; name: string }
  | { status: "loading" }
  | { status: "unresolved" };

export interface IntentResolver {
  resolve(kind: string, id: string): EntityResolution;
}

interface SchemaLike {
  properties?: Record<string, SchemaLike & { "x-selector"?: string; title?: string }>;
  "x-selector"?: string;
  "x-intent"?: unknown;
  "x-intentPlaceholders"?: unknown;
}

const asSchema = (s: unknown): SchemaLike => (s && typeof s === "object" ? (s as SchemaLike) : {});

/** Walk a schema's properties (recursing into nested objects) recording each field's x-selector by dotted path. */
function collectSelectors(schema: SchemaLike, prefix: string, out: Map<string, string>): void {
  const props = schema.properties;
  if (!props) return;
  for (const [key, prop] of Object.entries(props)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof prop["x-selector"] === "string") out.set(path, prop["x-selector"]);
    if (prop.properties) collectSelectors(prop, path, out);
  }
}

function getByPath(obj: Record<string, unknown> | undefined, path: string): unknown {
  if (!obj) return undefined;
  return path.split(".").reduce<unknown>(
    (acc, k) => (acc && typeof acc === "object" ? (acc as Record<string, unknown>)[k] : undefined),
    obj,
  );
}

/** Humanize the last path segment as a fallback prompt ("targetBranch" → "target branch"). */
function humanize(path: string): string {
  const last = path.split(".").pop() ?? path;
  return last.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").toLowerCase();
}

/** A bound {{ref}} → the inner path for a subtle chip ("{{trigger.branch}}" → "trigger.branch"). */
function formatRefLabel(raw: string): string {
  const m = raw.match(/\{\{\s*(.+?)\s*\}\}/);
  return m ? m[1] : raw;
}

export function composeIntent(
  configSchema: unknown,
  inputSchema: unknown,
  config: Record<string, unknown> | undefined,
  inputs: Record<string, unknown> | undefined,
  resolver: IntentResolver,
): IntentSegment[] | null {
  const cs = asSchema(configSchema);
  const template = cs["x-intent"];
  if (typeof template !== "string" || template.trim() === "") return null; // opt-out gate

  const prompts = (cs["x-intentPlaceholders"] && typeof cs["x-intentPlaceholders"] === "object"
    ? (cs["x-intentPlaceholders"] as Record<string, string>)
    : {});

  const selectors = new Map<string, string>();
  collectSelectors(cs, "", selectors);
  collectSelectors(asSchema(inputSchema), "", selectors);

  const readVal = (path: string): unknown => {
    const c = getByPath(config, path);
    return c !== undefined ? c : getByPath(inputs, path);
  };
  const promptFor = (path: string): string => prompts[path] ?? humanize(path);

  const segments: IntentSegment[] = [];
  const push = (seg: IntentSegment): void => {
    if (seg.type === "text") {
      if (seg.text === "") return;
      const last = segments[segments.length - 1];
      if (last && last.type === "text") { last.text += seg.text; return; }
    }
    segments.push(seg);
  };

  const re = /\{([^}]*)\}/g;
  let lastIdx = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(template)) !== null) {
    if (m.index > lastIdx) push({ type: "text", text: template.slice(lastIdx, m.index) });
    lastIdx = re.lastIndex;
    const token = m[1];

    // {flag?text} — emit text only when the boolean at `flag` is truthy.
    const q = token.indexOf("?");
    if (q >= 0) {
      const flag = token.slice(0, q).trim();
      if (readVal(flag)) push({ type: "text", text: token.slice(q + 1) });
      continue;
    }

    const path = token.trim();
    const raw = readVal(path);
    if (raw === undefined || raw === null || raw === "") {
      push({ type: "prompt", text: promptFor(path) });
      continue;
    }
    if (typeof raw === "string" && raw.includes("{{")) {
      push({ type: "ref", label: formatRefLabel(raw) }); // bound value resolves at run time — never a name
      continue;
    }
    const kind = selectors.get(path);
    if (kind) {
      const r = resolver.resolve(kind, String(raw));
      if (r.status === "resolved") push({ type: "entity", name: r.name, kind });
      else push({ type: "prompt", text: promptFor(path) }); // loading/unresolved → muted prompt, never a GUID
      continue;
    }
    push({ type: "text", text: String(raw) });
  }
  if (lastIdx < template.length) push({ type: "text", text: template.slice(lastIdx) });
  return segments;
}
