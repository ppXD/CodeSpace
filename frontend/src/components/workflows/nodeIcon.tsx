import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeManifestDto } from "@/api/workflows";

/**
 * The palette/picker icon for a node manifest — a Kind-then-Category cascade. Reading the declared
 * Category (not a typeKey-prefix probe) is the source of truth, so a plugin author who names their
 * LLM node "anthropic.chat" still gets the AI icon. Shared by the left palette AND the "+" node
 * picker so both show the same icon for a given node type.
 */
export function nodeIconFor(m: NodeManifestDto, size = 12) {
  if (m.kind === "Trigger") return <Ic.Zap size={size} />;
  if (m.kind === "Terminal") return <Ic.CircleStop size={size} />;
  if (m.category === "AI") return <Ic.Sparkles size={size} />;
  if (m.category === "Git") return <Ic.Branch size={size} />;
  return <Ic.Box size={size} />;
}
