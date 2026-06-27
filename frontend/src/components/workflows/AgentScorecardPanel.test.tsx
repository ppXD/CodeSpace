import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentRunScorecard, TeamCostRollup } from "@/api/agents";
import { AgentScorecardView } from "./AgentScorecardPanel";

/**
 * The scorecard view is the measurement-spine surface — it must render the team's REAL success rate + latency
 * percentiles + per-harness comparison faithfully (the API's job is to compute them; the view's job is to show
 * them without inventing anything). These pin: the headline stats, the per-harness rows, the duration
 * formatting, the estimated-spend stat — present as $X.XX when a cost rollup IS supplied, an em-dash when
 * estimatedCostUsd is null, and ABSENT entirely when no rollup is passed (never fabricated) — and the empty state.
 */
const card: AgentRunScorecard = {
  overall: { harness: "(all)", total: 4, succeeded: 3, successRate: 0.75, p50DurationSeconds: 20, p95DurationSeconds: 95 },
  harnesses: [
    { harness: "claude-code", total: 1, succeeded: 1, successRate: 1.0, p50DurationSeconds: 5, p95DurationSeconds: 5 },
    { harness: "codex-cli", total: 3, succeeded: 2, successRate: 2 / 3, p50DurationSeconds: 20, p95DurationSeconds: 95 },
  ],
};

describe("AgentScorecardView", () => {
  it("renders the overall headline stats — success rate, P50/P95 latency, runs scored", () => {
    render(<AgentScorecardView card={card} />);

    // Scope to the headline strip — the per-harness table repeats some of these values (e.g. P50=20s on
    // codex-cli), so the headline assertion must read its own region, paired with its label.
    const head = (label: string) => screen.getByText(label).parentElement!;

    // Success rate as a clean whole-number percentage.
    expect(within(head("Success rate")).getByText("75%")).toBeInTheDocument();
    // P50 = 20s (under a minute → seconds), P95 = 95s (over a minute → "1m 35s").
    expect(within(head("P50 latency")).getByText("20s")).toBeInTheDocument();
    expect(within(head("P95 latency")).getByText("1m 35s")).toBeInTheDocument();
    // Runs scored = succeeded / total.
    expect(within(head("Runs scored")).getByText("3/4")).toBeInTheDocument();
  });

  it("renders a per-harness comparison row for each harness", () => {
    render(<AgentScorecardView card={card} />);

    const table = screen.getByRole("table");
    expect(within(table).getByText("claude-code")).toBeInTheDocument();
    expect(within(table).getByText("codex-cli")).toBeInTheDocument();

    // claude-code: 100%, 1/1.  codex-cli: 67%, 2/3.
    expect(within(table).getByText("100%")).toBeInTheDocument();
    expect(within(table).getByText("67%")).toBeInTheDocument();
    expect(within(table).getByText("1/1")).toBeInTheDocument();
    expect(within(table).getByText("2/3")).toBeInTheDocument();
  });

  it("does NOT surface a cost figure when no cost rollup is supplied (the view never fabricates one)", () => {
    render(<AgentScorecardView card={card} />);

    expect(screen.queryByText(/cost/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/\$/)).not.toBeInTheDocument();
  });

  it("renders the estimated spend as a headline stat when a cost rollup IS supplied", () => {
    const cost: TeamCostRollup = { totalInputTokens: 1000, totalOutputTokens: 500, estimatedCostUsd: 12.4, runCount: 4, unknownCostRuns: 0, windowRunCount: 4, truncated: false };

    render(<AgentScorecardView card={card} cost={cost} />);

    const head = screen.getByText("Est. cost").parentElement!;
    expect(within(head).getByText("$12.40")).toBeInTheDocument();
  });

  it("renders an em-dash for cost when nothing in the window could be priced (null, not $0.00)", () => {
    const cost: TeamCostRollup = { totalInputTokens: 0, totalOutputTokens: 0, estimatedCostUsd: null, runCount: 0, unknownCostRuns: 2, windowRunCount: 2, truncated: false };

    render(<AgentScorecardView card={card} cost={cost} />);

    const head = screen.getByText("Est. cost").parentElement!;
    expect(within(head).getByText("—")).toBeInTheDocument();
  });

  it("shows the empty state when no runs have been scored yet", () => {
    const empty: AgentRunScorecard = { overall: { harness: "(all)", total: 0, succeeded: 0, successRate: 0, p50DurationSeconds: null, p95DurationSeconds: null }, harnesses: [] };

    render(<AgentScorecardView card={empty} />);

    expect(screen.getByText(/No runs scored yet/)).toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("renders an em-dash for a harness with no latency to show", () => {
    const noLatency: AgentRunScorecard = {
      overall: { harness: "(all)", total: 1, succeeded: 0, successRate: 0, p50DurationSeconds: null, p95DurationSeconds: null },
      harnesses: [{ harness: "codex-cli", total: 1, succeeded: 0, successRate: 0, p50DurationSeconds: null, p95DurationSeconds: null }],
    };

    render(<AgentScorecardView card={noLatency} />);

    // Both the headline P50/P95 and the row's latency cells fall back to an em-dash.
    expect(screen.getAllByText("—").length).toBeGreaterThanOrEqual(2);
  });
});
