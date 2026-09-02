/**
 * A non-secret reminder of WHICH key a destination uses, built from what the operator typed.
 *
 * The rule is deliberately narrow, because getting it wrong writes a secret into a field every screen displays. A
 * value is echoed only when the provider's schema actively DISTINGUISHES its secret fields — at least one field
 * marked `writeOnly: true` — and then only from a field it did not so mark. A schema that marks nothing has told us
 * nothing is safe to show, so nothing is shown, and the card falls back to naming the key instead.
 */
export function deriveSecretHint(secretSchema: unknown, secret: Record<string, unknown>): string | null {
  const properties = isRecord(secretSchema) && isRecord(secretSchema.properties) ? secretSchema.properties : {};
  const declarations = Object.entries(properties);
  const distinguishes = declarations.some(([, declared]) => isRecord(declared) && declared.writeOnly === true);
  if (!distinguishes) return null;

  for (const [name, declared] of declarations) {
    if (isRecord(declared) && declared.writeOnly === true) continue;
    const value = secret[name];
    if (typeof value === "string" && value.trim().length > 0) return mask(value.trim());
  }
  return null;
}

/** Enough of the value to recognise it, never enough to use it. */
function mask(value: string): string {
  return value.length <= 12 ? value : `${value.slice(0, 7)}…${value.slice(-4)}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
