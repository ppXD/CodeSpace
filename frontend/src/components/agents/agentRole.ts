/**
 * Heuristic role classification for an agent persona — display-only, derived from the name + description so the
 * bench can show a role badge + a role-tinted avatar without a backing field. First match wins, most specific
 * first; nothing matches → Generalist. This is a presentation hint, not a stored attribute: when a real role
 * field lands, swap the call site, not the consumers.
 */
export type AgentRole = "Architect" | "Reviewer" | "Tracer" | "Planner" | "Generalist";

const PATTERNS: ReadonlyArray<[AgentRole, RegExp]> = [
  ["Reviewer", /\b(review|audit|security|secure|vulnerab|qa|lint|critic)/i],
  ["Tracer", /\b(bug|triage|debug|repro|trace|incident|diagnos|root.?cause)/i],
  // `plan` is matched only in its planner forms (planner/planning), not as a bare prefix — an incidental
  // "Plans and designs" in an architect's description must not outrank the explicit "architect" token below.
  ["Planner", /\b(plan(ner|ning)|coordinat|orchestrat|roadmap|strateg|supervis|manager)/i],
  ["Architect", /\b(architect|design|backend|frontend|api|infra|system|database|schema|implement|build|engineer)/i],
];

/** Classify a persona's role from its name + description. Pure; safe on null/empty description. */
export function deriveRole(agent: { name: string; description: string | null }): AgentRole {
  const haystack = `${agent.name} ${agent.description ?? ""}`;

  for (const [role, pattern] of PATTERNS) {
    if (pattern.test(haystack)) return role;
  }

  return "Generalist";
}
