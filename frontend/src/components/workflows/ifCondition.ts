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

/** A right-hand token as the DSL wire-form (quoted string / ref / number / bool) → the plain text the editor shows. */
function unquote(s: string): string {
  if (s.length >= 2 && ((s[0] === '"' && s.endsWith('"')) || (s[0] === "'" && s.endsWith("'")))) return s.slice(1, -1);
  return s;
}

/** The plain text the editor holds → the DSL wire-form. A ref / number / bool passes through; a bare string is quoted. */
export function quoteRight(text: string): string {
  const v = text.trim();
  if (v === "") return v;
  if (v.startsWith("{{")) return v;                                  // a {{ref}} expression
  if (/^-?\d+(\.\d+)?$/.test(v)) return v;                            // a number literal
  if (["true", "false", "null"].includes(v.toLowerCase())) return v; // bool / null
  if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) return v; // already quoted
  return `"${v.replace(/"/g, '\\"')}"`;                              // a string literal
}

/** Expression string → structured form. The grammar is total, so every valid condition maps cleanly. */
export function parseCondition(raw: string | undefined): IfCondition {
  const t = (raw ?? "").trim();
  if (!t) return { left: "", op: "truthy", right: "" };

  for (const op of UNARY) {
    if (t.endsWith(` ${op}`)) return { left: t.slice(0, t.length - op.length - 1).trim(), op, right: "" };
  }
  for (const op of BINARY_BY_LENGTH) {
    const marker = ` ${op} `;
    const idx = t.indexOf(marker);
    if (idx >= 0) return { left: t.slice(0, idx).trim(), op, right: unquote(t.slice(idx + marker.length).trim()) };
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
