/**
 * The logic.if node stores its condition as ONE expression string the engine's ConditionEvaluator parses:
 *   <left> <op> <right>   ·   <left> is_empty|is_not_empty   ·   <left>   (bare truthiness)
 * Ordinary people can't hand-write that. The guided If editor edits it as three parts — a value, an operator
 * dropdown, a value/literal — and this pure module round-trips between the structured form and the exact
 * expression string the engine already understands (so it stays non-breaking and needs no engine change).
 */
export type IfArity = "binary" | "unary" | "truthy";

export interface IfOperator {
  /** The DSL token (or "truthy" for the bare-value case). */
  value: string;
  label: string;
  arity: IfArity;
}

export const IF_OPERATORS: IfOperator[] = [
  { value: "==", label: "equals", arity: "binary" },
  { value: "!=", label: "does not equal", arity: "binary" },
  { value: ">", label: "greater than", arity: "binary" },
  { value: ">=", label: "at least (≥)", arity: "binary" },
  { value: "<", label: "less than", arity: "binary" },
  { value: "<=", label: "at most (≤)", arity: "binary" },
  { value: "contains", label: "contains", arity: "binary" },
  { value: "startsWith", label: "starts with", arity: "binary" },
  { value: "endsWith", label: "ends with", arity: "binary" },
  { value: "is_not_empty", label: "is not empty", arity: "unary" },
  { value: "is_empty", label: "is empty", arity: "unary" },
  { value: "truthy", label: "is true", arity: "truthy" },
];

export interface IfCondition {
  left: string;
  /** An IF_OPERATORS value. */
  op: string;
  right: string;
}

const UNARY = ["is_empty", "is_not_empty"];
// Same order ConditionEvaluator uses (BinaryOps ordered by length desc, longest first) so `>=` wins over `>`
// and our split matches the engine's exactly.
const BINARY_BY_LENGTH = ["startsWith", "contains", "endsWith", "==", "!=", ">=", "<=", ">", "<"];

const NUMBER_RE = /^-?\d+(\.\d+)?$/;
const isNumberBoolRefOrEmpty = (v: string) =>
  v === "" || v.startsWith("{{") || NUMBER_RE.test(v) || ["true", "false", "null"].includes(v.toLowerCase());

/** A right-hand token as the DSL wire-form (quoted string / ref / number / bool) → the plain text the editor shows. */
function unquote(s: string): string {
  if (s.length >= 2 && ((s[0] === '"' && s.endsWith('"')) || (s[0] === "'" && s.endsWith("'")))) {
    const inner = s.slice(1, -1);
    // Keep the quotes when dropping them would change meaning on re-serialize: quoteRight passes an empty
    // string / number / bool·null / {{ref}} through UNQUOTED, so a quoted "" / "01234" / "true" / "{{x}}" would
    // otherwise round-trip as a dropped comparison / a number / a bool / a ref. Keeping the quotes is byte-exact.
    return isNumberBoolRefOrEmpty(inner) ? s : inner;
  }
  return s;
}

/** The plain text the editor holds → the DSL wire-form. A ref / number / bool passes through; a bare string is quoted. */
export function quoteRight(text: string): string {
  const v = text.trim();
  if (v === "") return v;
  if (v.startsWith("{{")) return v;                                  // a {{ref}} expression
  if (NUMBER_RE.test(v)) return v;                                   // a number literal
  if (["true", "false", "null"].includes(v.toLowerCase())) return v; // bool / null
  if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) return v; // already quoted
  // The engine strips only the OUTER quotes (no unescape — ConditionEvaluator.Resolve does token[1..^1]), so
  // an escaped \" would survive verbatim into the compared value. Emit a naive wrap; the engine's own strip
  // handles an embedded quote symmetrically (`"a"b"` → `a"b`), keeping the round-trip byte-exact.
  return `"${v}"`;                                                   // a string literal
}

/** Replace every char inside a quoted span (delimiters included) with a filler, preserving length/positions, so
 *  an operator token INSIDE a string literal (`"release contains fix"`) can't be chosen as the split point. */
function maskQuotes(s: string): string {
  let out = "";
  let quote: string | null = null;
  for (const c of s) {
    if (quote) { out += "\x01"; if (c === quote) quote = null; }
    else if (c === '"' || c === "'") { quote = c; out += "\x01"; }
    else out += c;
  }
  return out;
}

/** Expression string → structured form. The operator search runs over the quote-MASKED string so a literal
 *  containing an op token doesn't mis-split; indices map back to the original. The grammar is otherwise total. */
export function parseCondition(raw: string | undefined): IfCondition {
  const t = (raw ?? "").trim();
  if (!t) return { left: "", op: "truthy", right: "" };
  const masked = maskQuotes(t);

  for (const op of UNARY) {
    if (masked.endsWith(` ${op}`)) return { left: t.slice(0, t.length - op.length - 1).trim(), op, right: "" };
  }
  for (const op of BINARY_BY_LENGTH) {
    const idx = masked.indexOf(` ${op} `);
    if (idx >= 0) return { left: t.slice(0, idx).trim(), op, right: unquote(t.slice(idx + op.length + 2).trim()) };
  }
  return { left: t, op: "truthy", right: "" };
}

/** Structured form → the expression string the engine evaluates. */
export function serializeCondition(c: IfCondition): string {
  const left = c.left.trim();
  if (!left) return "";
  const def = IF_OPERATORS.find((o) => o.value === c.op);
  if (!def || def.arity === "truthy") return left;
  if (def.arity === "unary") return `${left} ${c.op}`;
  const right = quoteRight(c.right);
  // A binary op with no right value yet isn't a valid comparison — degrade to bare `left` (truthiness)
  // rather than emit a half-expression the engine would mis-parse.
  return right === "" ? left : `${left} ${c.op} ${right}`;
}

/** The operators that make sense for the left value's type — so a boolean only offers is-true / equals, etc. */
export function allowedOperators(type: string | undefined): IfOperator[] {
  const base = type?.split("|")[0].trim();
  const pick = (values: string[]) => IF_OPERATORS.filter((o) => values.includes(o.value));
  switch (base) {
    case "boolean": return pick(["truthy", "==", "!="]);
    case "integer":
    case "number": return pick(["==", "!=", ">", ">=", "<", "<=", "is_empty", "is_not_empty"]);
    case "string": return pick(["==", "!=", "contains", "startsWith", "endsWith", "is_empty", "is_not_empty"]);
    case "array": return pick(["is_empty", "is_not_empty"]);
    default: return IF_OPERATORS;
  }
}
