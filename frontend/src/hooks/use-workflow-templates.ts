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

export function useCreateAiCodeReviewWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => {
      const input: CreateWorkflowInput = {
        name: "AI Code Review",
        description: "When a pull request is opened, fetch the diff, ask an LLM for a review, and post it as a PR comment.",
        enabled: true,
        definition: AI_CODE_REVIEW,
        activations: [{ typeKey: "trigger.pr.opened", enabled: true, config: {} }],
      };
      return workflowsApi.create(input);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}
