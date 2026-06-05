import { useMutation, useQueryClient } from "@tanstack/react-query";

import { workflowsApi, type CreateWorkflowInput, type WorkflowDefinition } from "@/api/workflows";

/**
 * Seed definitions for "+ Add workflow". The primary button uses the EMPTY seed — one
 * trigger node + one terminal node, connected, ready for the user to drag in the steps
 * they actually want. The AI Code Review template stays available as a separate "Start
 * from template" affordance the editor can surface later.
 */

const EMPTY_DEFINITION: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    {
      id: "trigger",
      typeKey: "trigger.pr.opened",
      label: "When PR opened",
      config: {},
      inputs: {},
      position: { x: 80, y: 80 },
    },
    {
      id: "end",
      typeKey: "builtin.terminal",
      label: "Done",
      config: {},
      inputs: {},
      position: { x: 80, y: 260 },
    },
  ],
  edges: [{ from: "trigger", to: "end" }],
};

const AI_CODE_REVIEW: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    { id: "trigger", typeKey: "trigger.pr.opened", label: "When PR opened", config: {}, inputs: {}, position: { x: 80, y: 60 } },
    {
      id: "fetch_diff",
      typeKey: "git.fetch_pr_diff",
      label: "Fetch PR diff",
      config: {},
      inputs: { repositoryId: "{{trigger.repositoryId}}", number: "{{trigger.number}}" },
      position: { x: 80, y: 220 },
    },
    {
      id: "review_llm",
      typeKey: "llm.complete",
      label: "Ask LLM for review",
      config: { provider: "Anthropic", model: "claude-sonnet-4-5", maxTokens: 3000, temperature: 0.2 },
      inputs: {
        systemPrompt:
          "You are a senior software engineer reviewing a pull request. Focus on: correctness, edge cases, naming, security, and obvious refactors. Be specific — call out file paths and line numbers when you can. Output GitHub-flavoured markdown with a short Summary section followed by a bullet list of comments. Keep it under 30 lines unless the diff is massive.",
        userPrompt:
          "PR title: {{trigger.title}}\n\nPR description:\n{{trigger.body}}\n\nDiff:\n{{nodes.fetch_diff.outputs.files}}",
      },
      position: { x: 80, y: 380 },
    },
    {
      id: "post_comment",
      typeKey: "git.post_pr_comment",
      label: "Post review comment",
      config: {},
      inputs: {
        repositoryId: "{{trigger.repositoryId}}",
        number: "{{trigger.number}}",
        body: "{{nodes.review_llm.outputs.text}}",
      },
      position: { x: 80, y: 540 },
    },
    { id: "end", typeKey: "builtin.terminal", label: "Done", config: {}, inputs: {}, position: { x: 80, y: 700 } },
  ],
  edges: [
    { from: "trigger", to: "fetch_diff" },
    { from: "fetch_diff", to: "review_llm" },
    { from: "review_llm", to: "post_comment" },
    { from: "post_comment", to: "end" },
  ],
};

// On PR opened: fetch diff → AI review → an APPROVAL CARD (2 approvals, any "request changes" blocks,
// auto-requests-changes after 24h with no decision) → submit that verdict back to the PR as the
// reviewer who clicked. The closed loop: code → AI summary → human decision → native git approve.
const AI_PR_REVIEW_GATE: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    { id: "trigger", typeKey: "trigger.pr.opened", label: "When PR opened", config: {}, inputs: {}, position: { x: 80, y: 40 } },
    {
      id: "fetch_diff",
      typeKey: "git.fetch_pr_diff",
      label: "Fetch PR diff",
      config: {},
      inputs: { repositoryId: "{{trigger.repositoryId}}", number: "{{trigger.number}}" },
      position: { x: 80, y: 200 },
    },
    {
      id: "review_llm",
      typeKey: "llm.complete",
      label: "AI review",
      config: { provider: "Anthropic", model: "claude-sonnet-4-5", maxTokens: 3000, temperature: 0.2 },
      inputs: {
        systemPrompt:
          "You are a senior engineer reviewing a pull request. In under 12 lines of GitHub-flavoured markdown, summarise the risk — correctness, security, edge cases — and whether it's safe to merge. End with a one-line recommendation.",
        userPrompt: "PR title: {{trigger.title}}\n\nDescription:\n{{trigger.body}}\n\nDiff:\n{{nodes.fetch_diff.outputs.files}}",
      },
      position: { x: 80, y: 360 },
    },
    {
      id: "review_gate",
      typeKey: "chat.post_message",
      label: "Approval gate",
      // Quorum of 2; a "request changes" vetoes immediately; if undecided in 24h it auto-resolves as
      // request_changes (safe default — never silently approves an un-reviewed PR, never hangs).
      config: { waitForResponse: true, resolve: { mode: "quorum", count: 2, deadlineSeconds: 86400, onTimeout: "request_changes" } },
      // conversationId is intentionally omitted — pick a channel after creating, then enable the workflow.
      inputs: {
        body:
          "🔍 **PR #{{trigger.number}} — {{trigger.title}}**\nNeeds 2 approvals; any “Request changes” blocks. Auto-requests changes if undecided in 24h.\n\n{{nodes.review_llm.outputs.text}}",
        actions: [
          { key: "approve", label: "✅ Approve", style: "Primary" },
          { key: "request_changes", label: "🛑 Request changes", style: "Danger", vetoes: true, requiresComment: true },
        ],
      },
      position: { x: 80, y: 520 },
    },
    {
      id: "submit_review",
      typeKey: "git.pr_review",
      label: "Submit review",
      config: {},
      // The clicked verdict (approve / request_changes) writes back to the PR AS the person who clicked
      // (actAsUserId = their linked identity); their comment becomes the review body.
      inputs: {
        repositoryId: "{{trigger.repositoryId}}",
        number: "{{trigger.number}}",
        verdict: "{{nodes.review_gate.outputs.action}}",
        body: "{{nodes.review_gate.outputs.comment}}",
        actAsUserId: "{{nodes.review_gate.outputs.by}}",
      },
      position: { x: 80, y: 700 },
    },
    { id: "end", typeKey: "builtin.terminal", label: "Done", config: {}, inputs: {}, position: { x: 80, y: 860 } },
  ],
  edges: [
    { from: "trigger", to: "fetch_diff" },
    { from: "fetch_diff", to: "review_llm" },
    { from: "review_llm", to: "review_gate" },
    { from: "review_gate", to: "submit_review" },
    { from: "submit_review", to: "end" },
  ],
};

/** A named starter workflow surfaced under "Start from template". Data-driven — add an entry to ship a new one. */
export interface WorkflowTemplate {
  id: string;
  name: string;
  description: string;
  definition: WorkflowDefinition;
  activations: CreateWorkflowInput["activations"];
  /** Created enabled? Templates that need a one-time config (e.g. pick a channel) ship disabled. */
  enabled: boolean;
}

export const WORKFLOW_TEMPLATES: WorkflowTemplate[] = [
  {
    id: "ai-code-review",
    name: "AI Code Review",
    description: "On PR opened: fetch the diff, ask an LLM for a review, post it as a PR comment.",
    definition: AI_CODE_REVIEW,
    activations: [{ typeKey: "trigger.pr.opened", enabled: true, config: {} }],
    enabled: true,
  },
  {
    id: "ai-pr-review-gate",
    name: "AI PR Review + Approval Gate",
    description: "AI review → an approval card (2 approvals, any block, 24h deadline) → submit the verdict back to the PR. Pick a channel, then enable.",
    definition: AI_PR_REVIEW_GATE,
    activations: [{ typeKey: "trigger.pr.opened", enabled: true, config: {} }],
    enabled: false, // needs a conversation picked first
  },
];

export function useCreateEmptyWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => {
      const input: CreateWorkflowInput = {
        name: "Untitled workflow",
        description: null,
        enabled: false,
        definition: EMPTY_DEFINITION,
        activations: [],
      };
      return workflowsApi.create(input);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}

/** Create a workflow from any <see cref="WorkflowTemplate"/> — one generic mutation drives every entry
 * in WORKFLOW_TEMPLATES (no per-template hook to maintain). */
export function useCreateWorkflowFromTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (template: WorkflowTemplate) => {
      const input: CreateWorkflowInput = {
        name: template.name,
        description: template.description,
        enabled: template.enabled,
        definition: template.definition,
        activations: template.activations,
      };
      return workflowsApi.create(input);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}
