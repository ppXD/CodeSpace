import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeKind } from "@/api/workflows";

/**
 * The minimal shape both a node manifest (palette / "+" picker) and a placed node's data (canvas card)
 * satisfy, so ONE resolver drives the icon + colour tone on every surface: add a node to the manifest and
 * it looks identical in the palette, the picker, and on the canvas with no per-surface code.
 */
export interface NodeVisual {
  typeKey: string;
  kind: NodeKind;
  category: string;
  iconKey?: string | null;
}

/** Manifest iconKey hint → icon. The author's hint always wins over the type/category fallbacks below. */
const ICON_BY_KEY: Record<string, typeof Ic.Box> = {
  "git-pull-request": Ic.PrOpen,
  "git-commit-horizontal": Ic.Commit,
  "file-diff": Ic.Code,
  "message-square": Ic.Chat,
  sparkles: Ic.Sparkles,
  "circle-stop": Ic.CircleStop,
  zap: Ic.Zap,
  play: Ic.Play,
  workflow: Ic.Workflow,
};

/**
 * Per-type icon so each builtin reads at a glance ("一看就知道這是什麼"). A fallback layer, not a registry:
 * a node whose typeKey isn't listed drops to its category, then Kind — so a new or plugin node still gets a
 * sensible glyph with no edit here. The manifest's own iconKey hint (above) still wins.
 */
const ICON_BY_TYPE: Record<string, typeof Ic.Box> = {
  "trigger.manual": Ic.Play,
  "trigger.schedule": Ic.Clock,
  "trigger.pr.opened": Ic.PrOpen,
  "trigger.pr.updated": Ic.PrDraft,
  "trigger.pr.merged": Ic.PrMerged,
  "trigger.push": Ic.Commit,
  "logic.if": Ic.Fork,
  "logic.merge": Ic.Sort,
  "flow.map": Ic.Copy,
  "flow.loop": Ic.Sync,
  "flow.iterate": Ic.Copy,
  "flow.try": Ic.Shield,
  "flow.sleep": Ic.Clock,
  "flow.subworkflow": Ic.Workflow,
  "flow.decision": Ic.Compass,
  "flow.wait_approval": Ic.Bell,
  "flow.wait_action": Ic.Bell,
  "flow.wait_callback": Ic.Link,
  "http.request": Ic.Globe,
  "git.open_pr": Ic.PrOpen,
  "git.merge_pr": Ic.PrMerged,
  "git.pr_review": Ic.Eye,
  "git.post_pr_comment": Ic.Chat,
  "git.fetch_pr_diff": Ic.Code,
  "git.fetch_pr_checks": Ic.Check,
  "git.list_prs": Ic.Filter,
  "git.create_issue": Ic.IssueOpen,
  "git.comment_issue": Ic.Chat,
  "git.close_issue": Ic.IssueClosed,
  "git.integrate": Ic.Fork,
  "git.open_change_set": Ic.Branch,
  "agent.run": Ic.Bot,
  "agent.run_command": Ic.Command,
  "agent.supervisor": Ic.Team,
  "llm.complete": Ic.Sparkles,
  "plan.author": Ic.Milestone,
  "plan.confirm": Ic.Check,
  "chat.post_message": Ic.Chat,
  "builtin.terminal": Ic.CircleStop,
};

/** Category → icon, the layer below typeKey. Keyed on the manifest's declared category string. */
const ICON_BY_CATEGORY: Record<string, typeof Ic.Box> = {
  Triggers: Ic.Zap,
  AI: Ic.Sparkles,
  Agent: Ic.Bot,
  Planning: Ic.Milestone,
  Git: Ic.Branch,
  Logic: Ic.Workflow,
  Chat: Ic.Chat,
  Tools: Ic.Wrench,
};

/**
 * The icon for a node — author's iconKey hint → per-type → per-category → Kind → generic box. Each layer
 * is a fallback, so an unmapped / plugin node always lands on a sensible glyph without a code change here.
 * Shared by the canvas node card, the left palette, and the "+" picker so a type looks the same everywhere.
 */
export function nodeIconFor(m: NodeVisual, size = 12) {
  const Icon =
    (m.iconKey ? ICON_BY_KEY[m.iconKey] : undefined)
    ?? ICON_BY_TYPE[m.typeKey]
    ?? ICON_BY_CATEGORY[m.category]
    ?? (m.kind === "Trigger" ? Ic.Zap
      : m.kind === "Terminal" ? Ic.CircleStop
      : m.kind === "Map" ? Ic.Copy
      : Ic.Box);

  return <Icon size={size} />;
}

/**
 * A node's colour "tone" — a small, stable set derived from its category/kind. Set as `data-tone` on a node
 * card / palette row so CSS can tint the left rail + icon chip, letting nodes read by colour at a glance.
 * Generic: an unknown category falls to the neutral "tool" tone, so a plugin node still paints sanely.
 */
export function nodeToneFor(m: NodeVisual): string {
  if (m.kind === "Terminal") return "end";
  if (m.kind === "Trigger" || m.category === "Triggers") return "start";
  switch (m.category) {
    case "AI":
    case "Agent":
    case "Planning":
      return "ai";
    case "Git":
      return "git";
    case "Logic":
      return "flow";
    case "Chat":
      return "human";
    default:
      return "tool";
  }
}

/** The badge-driving manifest flags — a manifest DTO and a placed node's data both carry these (optional). */
export interface NodeBadgeSource {
  isSideEffecting?: boolean;
  canSuspend?: boolean;
  alwaysRequiresApproval?: boolean;
}

export interface NodeBadge {
  kind: "write" | "wait" | "approval";
  label: string;
}

/**
 * The "what does this step do" badges shown on a node card / palette row, derived from manifest flags so a
 * node opts in purely by its manifest — no per-node UI. Order = most consequential first (approval → write →
 * wait). A plain node (none set) gets no badges, so the canvas stays quiet except where a step actually acts.
 */
export function nodeBadges(m: NodeBadgeSource): NodeBadge[] {
  const badges: NodeBadge[] = [];
  if (m.alwaysRequiresApproval) badges.push({ kind: "approval", label: "Approval" });
  if (m.isSideEffecting) badges.push({ kind: "write", label: "Writes" });
  if (m.canSuspend) badges.push({ kind: "wait", label: "Waits" });
  return badges;
}
