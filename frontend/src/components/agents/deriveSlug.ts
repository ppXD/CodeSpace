/**
 * Mirrors the backend `AgentDefinitionService.DeriveSlug` so the editor can PREVIEW the @-mention handle a
 * name will produce (the server is authoritative; this is display-only). Lowercase, ASCII [a-z0-9_] kept,
 * every other run collapses to a single hyphen, leading/trailing hyphens trimmed, capped at 64. Returns ""
 * when no character survives (the name yields no usable handle — the editor warns before save).
 */
export function deriveSlug(name: string): string {
  let out = "";
  let lastWasHyphen = true; // suppresses a leading hyphen

  for (const ch of name) {
    if (/[A-Za-z0-9_]/.test(ch)) {
      out += ch.toLowerCase();
      lastWasHyphen = false;
    } else if (!lastWasHyphen) {
      out += "-";
      lastWasHyphen = true;
    }
  }

  out = out.replace(/-+$/, "");
  return out.length <= 64 ? out : out.slice(0, 64).replace(/-+$/, "");
}
